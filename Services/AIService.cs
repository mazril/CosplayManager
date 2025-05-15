// Plik: CosplayManager/Services/AIService.cs
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CosplayManager.Services
{
    public class AIService : IDisposable
    {
        private InferenceSession? _onnxSession;
        private readonly IImageHash _perceptualHashAlgorithm;
        private bool disposedValue;

        public string? CurrentModelPath { get; private set; }

        public AIService()
        {
            _perceptualHashAlgorithm = new PerceptualHash();
            CurrentModelPath = null;
            SimpleFileLogger.Log("AIService instance created.");
        }

        public bool IsOnnxModelLoaded() => _onnxSession != null;

        public void InitializeOnnxSession(string modelPath, int gpuDeviceId = 0)
        {
            SimpleFileLogger.Log($"AIService: Attempting to initialize ONNX session for model: '{Path.GetFileName(modelPath)}' (Full Path: {modelPath}) on GPU: {gpuDeviceId}");
            if (string.IsNullOrWhiteSpace(modelPath)) { SimpleFileLogger.LogError("AIService.InitializeOnnxSession: Model path cannot be empty."); throw new ArgumentNullException(nameof(modelPath), "Ścieżka do modelu nie może być pusta."); }
            if (!File.Exists(modelPath)) { SimpleFileLogger.LogError($"AIService.InitializeOnnxSession: Model file not found: {modelPath}"); _onnxSession?.Dispose(); _onnxSession = null; CurrentModelPath = null; throw new FileNotFoundException("Plik modelu ONNX nie został znaleziony.", modelPath); }
            if (_onnxSession != null && CurrentModelPath == modelPath) { SimpleFileLogger.Log($"AIService: Model '{Path.GetFileName(modelPath)}' is already loaded."); return; }
            if (_onnxSession != null) { SimpleFileLogger.Log($"AIService: Disposing previous ONNX session for model '{Path.GetFileName(CurrentModelPath ?? "unknown")}'."); _onnxSession.Dispose(); _onnxSession = null; CurrentModelPath = null; }

            Exception? gpuException = null;
            try
            {
                var sessionOptions = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING };
                sessionOptions.AppendExecutionProvider_CUDA(gpuDeviceId);
                SimpleFileLogger.Log($"AIService: Attempting GPU session for '{Path.GetFileName(modelPath)}'.");
                _onnxSession = new InferenceSession(modelPath, sessionOptions); CurrentModelPath = modelPath;
                SimpleFileLogger.Log($"AIService: Successfully initialized ONNX session on GPU for: '{Path.GetFileName(modelPath)}'."); return;
            }
            catch (Exception ex) { gpuException = ex; SimpleFileLogger.LogError($"AIService.InitializeOnnxSession: GPU initialization failed for '{Path.GetFileName(modelPath)}'. Attempting CPU.", ex); }
            try
            {
                var cpuSessionOptions = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING };
                SimpleFileLogger.Log($"AIService: Attempting CPU session for '{Path.GetFileName(modelPath)}'.");
                _onnxSession = new InferenceSession(modelPath, cpuSessionOptions); CurrentModelPath = modelPath;
                SimpleFileLogger.Log($"AIService: Successfully initialized ONNX session on CPU for: '{Path.GetFileName(modelPath)}'.");
            }
            catch (Exception cpuEx) { SimpleFileLogger.LogError($"AIService.InitializeOnnxSession: CPU initialization also failed for '{Path.GetFileName(modelPath)}'.", cpuEx); _onnxSession = null; CurrentModelPath = null; throw new InvalidOperationException($"Nie można zainicjować sesji ONNX dla modelu: '{Path.GetFileName(modelPath)}'. GPU Error: {gpuException?.Message ?? "N/A"}", cpuEx); }
        }

        public ulong? CalculatePerceptualHash(string imagePath)
        {
            try { using var stream = File.OpenRead(imagePath); return _perceptualHashAlgorithm.Hash(stream); }
            catch (Exception ex) { SimpleFileLogger.LogError($"CalculatePerceptualHash: Error for {imagePath}", ex); return null; }
        }

        public double ComparePerceptualHashes(ulong hash1, ulong hash2) => CompareHash.Similarity(hash1, hash2);

        private float[]? RunOnnxModelAndExtractFeaturesSync(Dictionary<string, OrtValue> inputs, string featureOutputName)
        {
            if (_onnxSession == null) { SimpleFileLogger.Log("RunOnnxModelAndExtractFeaturesSync: ONNX session is null."); return null; }
            try
            {
                using var runOptions = new RunOptions();
                using IDisposableReadOnlyCollection<OrtValue> sessionOutputs = _onnxSession.Run(runOptions, inputs, new List<string> { featureOutputName });
                OrtValue? firstOutputValue = sessionOutputs.FirstOrDefault();
                if (firstOutputValue != null && firstOutputValue.IsTensor) { ReadOnlySpan<float> tensorSpan = firstOutputValue.GetTensorDataAsSpan<float>(); return tensorSpan.ToArray(); }
                else { SimpleFileLogger.Log($"RunOnnxModelAndExtractFeaturesSync: Could not get output tensor for '{featureOutputName}'."); return null; }
            }
            catch (Exception ex) { SimpleFileLogger.LogError($"RunOnnxModelAndExtractFeaturesSync: Error running model for output '{featureOutputName}'.", ex); return null; }
        }

        public async Task<float[]?> GetImageFeatureVectorAsync(string imagePath)
        {
            if (_onnxSession == null) { SimpleFileLogger.Log($"GetImageFeatureVectorAsync: ONNX session not initialized for {Path.GetFileName(imagePath)}."); return null; }
            SimpleFileLogger.Log($"GetImageFeatureVectorAsync: Processing image {Path.GetFileName(imagePath)}");
            Image<Rgb24>? image = null;
            try
            {
                var inputName = _onnxSession.InputMetadata.Keys.First();
                var inputMetadata = _onnxSession.InputMetadata[inputName];
                var modelInputDimensions = inputMetadata.Dimensions;
                if (modelInputDimensions.Length != 4) { SimpleFileLogger.LogError($"GetImageFeatureVectorAsync: Model input '{inputName}' not 4D for {Path.GetFileName(imagePath)}."); return null; }
                int modelExpectedHeight = modelInputDimensions[2]; int modelExpectedWidth = modelInputDimensions[3];
                if (modelExpectedHeight <= 0 || modelExpectedWidth <= 0) { SimpleFileLogger.LogError($"GetImageFeatureVectorAsync: Model input '{inputName}' invalid H/W for {Path.GetFileName(imagePath)}."); return null; }
                SimpleFileLogger.Log($"GetImageFeatureVectorAsync: Model '{Path.GetFileName(CurrentModelPath)}' expects H={modelExpectedHeight}, W={modelExpectedWidth} for '{inputName}'.");

                image = await Task.Run(() => Image.Load<Rgb24>(imagePath));
                image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(modelExpectedWidth, modelExpectedHeight), Mode = ResizeMode.Crop }));
                var inputTensor = new DenseTensor<float>(new[] { 1, 3, modelExpectedHeight, modelExpectedWidth });
                if (image.Frames.Count > 0)
                {
                    var pixelData = new byte[modelExpectedWidth * modelExpectedHeight * 3];
                    image.CopyPixelDataTo(pixelData);
                    var mean = new float[] { 0.485f, 0.456f, 0.406f }; var stdDev = new float[] { 0.229f, 0.224f, 0.225f };
                    for (int y = 0; y < modelExpectedHeight; y++) { for (int x = 0; x < modelExpectedWidth; x++) { int idx = (y * modelExpectedWidth + x) * 3; inputTensor[0, 0, y, x] = (pixelData[idx] / 255f - mean[0]) / stdDev[0]; inputTensor[0, 1, y, x] = (pixelData[idx + 1] / 255f - mean[1]) / stdDev[1]; inputTensor[0, 2, y, x] = (pixelData[idx + 2] / 255f - mean[2]) / stdDev[2]; } }
                }
                else { SimpleFileLogger.Log($"GetImageFeatureVectorAsync: Image '{Path.GetFileName(imagePath)}' has no frames."); return null; }

                // *** POPRAWKA: Ręczna konwersja ReadOnlySpan<int> na long[] ***
                ReadOnlySpan<int> dimsSpan = inputTensor.Dimensions;
                long[] dimensions = new long[dimsSpan.Length];
                for (int i = 0; i < dimsSpan.Length; i++)
                {
                    dimensions[i] = dimsSpan[i];
                }
                // *** KONIEC POPRAWKI ***

                using var inputOrtValue = OrtValue.CreateTensorValueFromMemory<float>(inputTensor.Buffer.ToArray(), dimensions);
                var inputs = new Dictionary<string, OrtValue> { { inputName, inputOrtValue } };
                var outputNames = _onnxSession.OutputMetadata.Keys.ToList();
                string featureOutputName = outputNames.FirstOrDefault(name => name.Contains("pool", StringComparison.OrdinalIgnoreCase) || name.Contains("feature", StringComparison.OrdinalIgnoreCase) || name.Contains("embedding", StringComparison.OrdinalIgnoreCase)) ?? outputNames.Last();

                float[]? featureVector = await Task.Run(() => RunOnnxModelAndExtractFeaturesSync(inputs, featureOutputName));
                if (featureVector == null) { SimpleFileLogger.Log($"GetImageFeatureVectorAsync: Feature vector NULL for {Path.GetFileName(imagePath)} from output '{featureOutputName}'."); }
                else { SimpleFileLogger.Log($"GetImageFeatureVectorAsync: Extracted feature vector (len: {featureVector.Length}) for {Path.GetFileName(imagePath)}."); }
                return featureVector;
            }
            catch (Exception ex) { SimpleFileLogger.LogError($"GetImageFeatureVectorAsync: Error processing image '{Path.GetFileName(imagePath)}'.", ex); return null; }
            finally { image?.Dispose(); }
        }

        public float CalculateCosineSimilarity(ReadOnlySpan<float> vecA, ReadOnlySpan<float> vecB)
        {
            if (vecA.Length == 0 || vecB.Length == 0) { SimpleFileLogger.Log("CalculateCosineSimilarity: One or both vectors are empty, returning 0 similarity."); return 0f; }
            if (vecA.Length != vecB.Length) { SimpleFileLogger.LogError($"CalculateCosineSimilarity: Vector length mismatch ({vecA.Length} vs {vecB.Length}). Similarity will be incorrect."); return -1f; }

            float dotProduct = 0f; float magA = 0f; float magB = 0f;
            for (int i = 0; i < vecA.Length; i++) { dotProduct += vecA[i] * vecB[i]; magA += vecA[i] * vecA[i]; magB += vecB[i] * vecB[i]; }
            magA = (float)Math.Sqrt(magA); magB = (float)Math.Sqrt(magB);
            if (magA < 1e-6f || magB < 1e-6f) { SimpleFileLogger.Log($"CalculateCosineSimilarity: Magnitude of a vector is near zero (A:{magA}, B:{magB}). Returning 0 similarity to avoid NaN."); return 0f; }
            return dotProduct / (magA * magB);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue) { if (disposing) { SimpleFileLogger.Log("AIService: Disposing ONNX session."); _onnxSession?.Dispose(); _onnxSession = null; CurrentModelPath = null; } disposedValue = true; }
        }
        public void Dispose() { Dispose(disposing: true); GC.SuppressFinalize(this); }
    }
}