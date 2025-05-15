// Plik: Services/ClipServiceHttpClient.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CosplayManager.Services // Upewnij się, że to jest poprawna przestrzeń nazw dla Twojego projektu
{
    //region Modele DTO (Data Transfer Objects)
    // Umieszczone tutaj dla spójności z plikiem ClipServiceHttpClient.cs

    public class ClipImagePathInput
    {
        public string path { get; set; }
    }

    public class ClipImagePathsInput
    {
        public List<string> paths { get; set; }
    }

    public class ClipTextIn
    {
        public string text { get; set; }
    }

    public class ClipTextsIn
    {
        public List<string> texts { get; set; }
    }

    public class ClipEmbeddingResponse
    {
        public List<float> embedding { get; set; }
    }

    public class ClipEmbeddingsResponse
    {
        public List<List<float>> embeddings { get; set; }
    }

    public class ClipHealthResponse
    {
        public string status { get; set; }
        public bool embedder_fully_initialized { get; set; }
        public string effective_device { get; set; }
        public string details { get; set; }
    }
    //endregion

    public class ClipServiceHttpClient : IDisposable
    {
        private readonly string _pythonServerUrl;
        private readonly string _pathToPythonExecutable;
        private readonly string _pathToClipServerScript;
        private readonly string _clipServerWorkingDirectory;

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        private Process _pythonServerProcess;
        private bool _isDisposed = false;
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        public ClipServiceHttpClient(
            string pythonServerUrl = "http://127.0.0.1:8000",
            string pathToPythonExecutable = "python.exe",
            string pathToClipServerScript = "clip_server.py")
        {
            _pythonServerUrl = pythonServerUrl;
            _pathToPythonExecutable = pathToPythonExecutable; // Może wymagać pełnej ścieżki lub konfiguracji PATH

            // Jeśli pathToClipServerScript nie jest pełną ścieżką, rozwiąż ją względem katalogu aplikacji
            if (!Path.IsPathRooted(pathToClipServerScript))
            {
                _pathToClipServerScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pathToClipServerScript);
            }
            else
            {
                _pathToClipServerScript = pathToClipServerScript;
            }

            _clipServerWorkingDirectory = Path.GetDirectoryName(_pathToClipServerScript);

            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _jsonSerializerOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true // Dla dopasowania nazw właściwości (np. embedding vs Embedding)
            };
        }

        public async Task<bool> StartServerAsync(int startupTimeoutSeconds = 30)
        {
            if (await IsServerRunningAsync())
            {
                Console.WriteLine("Serwer Pythona już działa.");
                // Dodatkowo sprawdź, czy embedder jest w pełni zainicjalizowany
                var health = await GetHealthStatusAsync();
                if (health != null && health.embedder_fully_initialized)
                {
                    Console.WriteLine($"Serwer Pythona zgłasza pełną gotowość. Urządzenie: {health.effective_device}");
                    return true;
                }
                Console.WriteLine("Serwer Pythona działa, ale embedder może nie być w pełni gotowy. Próba restartu/sprawdzenia...");
                // Można rozważyć próbę zatrzymania i ponownego uruchomienia, jeśli nie jest w pełni gotowy
            }

            if (!File.Exists(_pathToClipServerScript))
            {
                Console.WriteLine($"KRYTYCZNY BŁĄD: Nie znaleziono skryptu serwera Pythona w: {_pathToClipServerScript}");
                // Tutaj można rzucić wyjątek lub obsłużyć błąd w logice aplikacji
                throw new FileNotFoundException("Nie znaleziono skryptu serwera Pythona.", _pathToClipServerScript);
            }
            if (!File.Exists(_pathToPythonExecutable) && _pathToPythonExecutable.Contains(Path.DirectorySeparatorChar)) // Sprawdź czy pełna ścieżka istnieje
            {
                // Jeśli 'python.exe' ma być z PATH, to File.Exists nie zadziała bezpośrednio
                // ale jeśli podano pełną ścieżkę, to powinna istnieć.
                // To sprawdzenie jest uproszczone. Lepsze byłoby próba uruchomienia 'python --version'.
                bool pythonExeInPath = true; // Załóżmy, że jest w PATH, jeśli nie podano pełnej ścieżki
                if (_pathToPythonExecutable.Contains(Path.DirectorySeparatorChar))
                {
                    pythonExeInPath = false; // Podano pełną ścieżkę
                }

                if (!pythonExeInPath && !File.Exists(_pathToPythonExecutable))
                {
                    Console.WriteLine($"KRYTYCZNY BŁĄD: Nie znaleziono interpretera Pythona w: {_pathToPythonExecutable}");
                    throw new FileNotFoundException("Nie znaleziono interpretera Pythona.", _pathToPythonExecutable);
                }
            }


            Console.WriteLine($"Próba uruchomienia serwera Pythona: \"{_pathToPythonExecutable}\" \"{_pathToClipServerScript}\" w katalogu \"{_clipServerWorkingDirectory}\"");
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = _pathToPythonExecutable,
                Arguments = $"\"{_pathToClipServerScript}\"",
                WorkingDirectory = _clipServerWorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                _pythonServerProcess = new Process { StartInfo = startInfo };
                _pythonServerProcess.OutputDataReceived += (sender, args) => { if (args.Data != null) Console.WriteLine($"[Python Output]: {args.Data}"); };
                _pythonServerProcess.ErrorDataReceived += (sender, args) => { if (args.Data != null) Console.WriteLine($"[Python Error]: {args.Data}"); };

                bool started = _pythonServerProcess.Start();
                if (!started)
                {
                    Console.WriteLine("Nie udało się uruchomić procesu serwera Pythona.");
                    _pythonServerProcess = null;
                    return false;
                }

                _pythonServerProcess.BeginOutputReadLine();
                _pythonServerProcess.BeginErrorReadLine();

                Console.WriteLine($"Proces serwera Pythona uruchomiony (PID: {_pythonServerProcess.Id}). Oczekiwanie na gotowość...");

                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < startupTimeoutSeconds)
                {
                    if (_pythonServerProcess.HasExited)
                    {
                        Console.WriteLine($"Proces serwera Pythona zakończył działanie przedwcześnie (kod wyjścia: {_pythonServerProcess.ExitCode}).");
                        _pythonServerProcess = null;
                        return false;
                    }
                    var healthStatus = await GetHealthStatusAsync();
                    if (healthStatus != null && healthStatus.status == "ok" && healthStatus.embedder_fully_initialized)
                    {
                        Console.WriteLine($"Serwer Pythona jest gotowy. Urządzenie: {healthStatus.effective_device}. Szczegóły: {healthStatus.details}");
                        return true;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                Console.WriteLine($"Serwer Pythona nie stał się gotowy w ciągu {startupTimeoutSeconds} sekund.");
                StopServer();
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Błąd podczas uruchamiania serwera Pythona: {ex.GetType().Name} - {ex.Message}");
                if (_pythonServerProcess != null && !_pythonServerProcess.HasExited)
                {
                    try { _pythonServerProcess.Kill(true); }
                    catch (InvalidOperationException) { /* Proces mógł już się zakończyć */ }
                }
                _pythonServerProcess = null;
                return false;
            }
        }

        public void StopServer()
        {
            if (_pythonServerProcess != null)
            {
                if (!_pythonServerProcess.HasExited)
                {
                    Console.WriteLine($"Zatrzymywanie procesu serwera Pythona (PID: {_pythonServerProcess.Id})...");
                    try
                    {
                        _pythonServerProcess.Kill(true);
                        _pythonServerProcess.WaitForExit(5000);
                        if (!_pythonServerProcess.HasExited)
                        {
                            Console.WriteLine("Proces serwera Pythona mógł nie zostać poprawnie zatrzymany.");
                        }
                        else
                        {
                            Console.WriteLine("Proces serwera Pythona zatrzymany.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Błąd podczas zatrzymywania serwera Pythona: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine("Proces serwera Pythona już był zakończony.");
                }
                _pythonServerProcess.Dispose();
                _pythonServerProcess = null;
            }
            else
            {
                Console.WriteLine("Serwer Pythona nie był uruchomiony lub już został zatrzymany.");
            }
        }

        private async Task<ClipHealthResponse> GetHealthStatusAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_pythonServerUrl}/health");
                if (response.IsSuccessStatusCode)
                {
                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<ClipHealthResponse>(jsonResponse, _jsonSerializerOptions);
                }
                return null; // Lub rzuć wyjątek / zwróć obiekt błędu
            }
            catch (HttpRequestException) { return null; }
            catch (JsonException ex)
            {
                Console.WriteLine($"Błąd deserializacji odpowiedzi z /health: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Nieoczekiwany błąd podczas sprawdzania /health: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> IsServerRunningAsync(bool checkEmbedderInitialization = false)
        {
            var healthStatus = await GetHealthStatusAsync();
            if (healthStatus == null) return false;

            if (checkEmbedderInitialization)
            {
                return healthStatus.status == "ok" && healthStatus.embedder_fully_initialized;
            }
            return healthStatus.status == "ok"; // Wystarczy, że serwer odpowiada
        }

        public async Task<float[]> GetImageEmbeddingFromPathAsync(string imagePath)
        {
            // Sprawdzenie, czy serwer działa i jest w pełni zainicjalizowany przed wysłaniem żądania
            if (!await IsServerRunningAsync(true))
            {
                Console.WriteLine("Serwer CLIP API nie jest uruchomiony lub embedder nie jest w pełni gotowy. Próba uruchomienia/restartu...");
                // Spróbuj uruchomić serwer (jeśli nie działa) lub poczekaj chwilę dłużej
                if (!await StartServerAsync()) // StartServerAsync wewnętrznie sprawdza /health
                {
                    throw new InvalidOperationException("Serwer CLIP API nie jest uruchomiony lub nie udało się go uruchomić z w pełni zainicjalizowanym embedderem.");
                }
            }


            var payload = new ClipImagePathInput { path = imagePath };
            var jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = null;
            try
            {
                response = await _httpClient.PostAsync($"{_pythonServerUrl}/get_image_embedding", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Błąd HTTP ({response.StatusCode}) z serwera CLIP: {errorContent}");
                    // Można spróbować zdeserializować ErrorResponse, jeśli serwer go zwraca
                    try
                    {
                        var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(errorContent, _jsonSerializerOptions); // Zakładając, że masz klasę ErrorResponse
                        throw new HttpRequestException($"Błąd API: {errorResponse?.detail ?? errorContent}", null, response.StatusCode);
                    }
                    catch (JsonException)
                    {
                        throw new HttpRequestException($"Błąd API ({response.StatusCode}): {errorContent}", null, response.StatusCode);
                    }
                }
                // response.EnsureSuccessStatusCode(); // Alternatywa, ale mniej kontroli nad treścią błędu

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var embeddingResponse = JsonSerializer.Deserialize<ClipEmbeddingResponse>(jsonResponse, _jsonSerializerOptions);

                return embeddingResponse?.embedding?.ToArray();
            }
            catch (HttpRequestException ex) // Już zalogowane lub rzucone z bloku wyżej
            {
                Console.WriteLine($"Błąd HttpRequestException: {ex.Message} (StatusCode: {ex.StatusCode})");
                throw;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Błąd deserializacji JSON odpowiedzi z serwera: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Nieoczekiwany błąd podczas GetImageEmbeddingFromPathAsync: {ex.GetType().Name} - {ex.Message}");
                throw;
            }
        }

        // TODO: Dodać metody dla przetwarzania wsadowego i tekstów, np.
        // public async Task<List<float[]>> GetImageEmbeddingsBatchFromPathsAsync(List<string> imagePaths) { ... }
        // public async Task<float[]> GetTextEmbeddingAsync(string text) { ... }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            if (disposing)
            {
                StopServer();
                // HttpClient jest statyczny, więc nie dysponujemy go tutaj. 
                // Jeśli nie byłby statyczny, tutaj byłoby _httpClient.Dispose();
            }
            _isDisposed = true;
        }

        ~ClipServiceHttpClient()
        {
            Dispose(false);
        }
    }

    // Dodatkowa klasa DTO dla odpowiedzi błędu z FastAPI, jeśli chcemy ją parsować
    public class ErrorResponse
    {
        public string detail { get; set; }
        public string type { get; set; } // Opcjonalne, jeśli serwer zwraca
    }
}