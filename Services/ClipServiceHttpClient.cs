// Plik: Services/ClipServiceHttpClient.cs
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CosplayManager.Services
{
    public class ClipServiceHttpClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseAddress = "http://127.0.0.1:8008"; // Upewnij się, że to port, na którym ręcznie uruchamiasz serwer Pythona
        private bool _isServerConfirmedRunning = false;

        public ClipServiceHttpClient()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(300) }; // Zwiększony timeout dla potencjalnie dłuższych operacji wsadowych
            SimpleFileLogger.Log("ClipServiceHttpClient: Konstruktor (tryb łączenia z istniejącym, zewnętrznym serwerem).");
        }

        public async Task<bool> EnsureServerConnectionAsync()
        {
            SimpleFileLogger.Log($"ClipServiceHttpClient: Próba potwierdzenia połączenia ze skonfigurowanym serwerem na {_baseAddress}");
            _isServerConfirmedRunning = await IsServerRunningAsync(checkEmbedderInitialization: true);

            if (_isServerConfirmedRunning)
            {
                SimpleFileLogger.LogHighLevelInfo("ClipServiceHttpClient: Pomyślnie połączono i zweryfikowano zewnętrzny serwer CLIP.");
            }
            else
            {
                SimpleFileLogger.LogError($"ClipServiceHttpClient: Nie udało się połączyć lub zweryfikować zewnętrznego serwera CLIP. Upewnij się, że serwer jest uruchomiony ręcznie na {_baseAddress} i odpowiada poprawnie na endpoint /health (w tym status embeddera).", null);
            }
            return _isServerConfirmedRunning;
        }

        public async Task<bool> IsServerRunningAsync(bool checkEmbedderInitialization = false)
        {
            try
            {
                SimpleFileLogger.Log($"IsServerRunningAsync: Sprawdzanie endpointu /health na {_baseAddress}");
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseAddress}/health");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // Zwiększony timeout dla health check
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(cts.Token);
                    SimpleFileLogger.Log($"IsServerRunningAsync: Odpowiedź z /health: {content}");
                    if (checkEmbedderInitialization)
                    {
                        try
                        {
                            var healthStatus = JsonSerializer.Deserialize<JsonElement>(content);
                            JsonElement statusElement;
                            JsonElement embedderStatusElement;

                            bool statusOk = healthStatus.TryGetProperty("status", out statusElement) &&
                                            statusElement.ValueKind == JsonValueKind.String &&
                                            statusElement.GetString()?.ToLowerInvariant() == "ok";

                            bool embedderInitialized = healthStatus.TryGetProperty("embedder_fully_initialized", out embedderStatusElement) &&
                                                       embedderStatusElement.ValueKind == JsonValueKind.True;

                            if (statusOk && embedderInitialized)
                            {
                                return true;
                            }
                            else
                            {
                                string? embedderStatusDetail = healthStatus.TryGetProperty("details", out var detailsElem) ? detailsElem.ToString() : "brak szczegółów";
                                string statusRead = statusOk ? statusElement.GetString() ?? "nieodczytany" : "nieodczytany/błędny";
                                string embedderInitRead = healthStatus.TryGetProperty("embedder_fully_initialized", out var embInitElem) ? embInitElem.ToString() : "brak klucza";

                                SimpleFileLogger.LogWarning($"IsServerRunningAsync: Health check OK, ale status embeddera nie jest w pełni zainicjalizowany. Status odczytany: '{statusRead}', EmbedderInitialized odczytany: '{embedderInitRead}', Szczegóły: {embedderStatusDetail}. Content: {content}");
                                return false;
                            }
                        }
                        catch (JsonException jsonEx)
                        {
                            SimpleFileLogger.LogError($"IsServerRunningAsync: Błąd deserializacji odpowiedzi z /health: {content}", jsonEx);
                            return false;
                        }
                    }
                    return true;
                }
                SimpleFileLogger.LogWarning($"IsServerRunningAsync: Health check nie powiódł się, status: {response.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                SimpleFileLogger.Log($"IsServerRunningAsync: Nie można połączyć się z serwerem CLIP (HttpRequestException): {ex.Message}");
            }
            catch (TaskCanceledException tex)
            {
                SimpleFileLogger.LogWarning($"IsServerRunningAsync: Timeout podczas łączenia z serwerem CLIP ({_baseAddress}/health): {tex.Message}");
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"IsServerRunningAsync: Nieoczekiwany błąd podczas sprawdzania statusu serwera.", ex);
            }
            return false;
        }

        public async Task<float[]?> GetImageEmbeddingFromPathAsync(string imagePath, CancellationToken cancellationToken = default)
        {
            if (!_isServerConfirmedRunning)
            {
                SimpleFileLogger.LogWarning($"GetImageEmbeddingFromPathAsync: Połączenie z serwerem CLIP nie jest potwierdzone. Próba ponownego sprawdzenia dla obrazu: {imagePath}");
                if (!await EnsureServerConnectionAsync())
                {
                    SimpleFileLogger.LogError($"GetImageEmbeddingFromPathAsync: Serwer CLIP nadal nie jest dostępny po ponownej próbie. Uruchom serwer ręcznie na {_baseAddress}. Nie można uzyskać embeddingu dla: {imagePath}", null);
                    return null;
                }
                SimpleFileLogger.LogHighLevelInfo($"GetImageEmbeddingFromPathAsync: Ponownie potwierdzono połączenie z serwerem CLIP przed żądaniem embeddingu.");
            }

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                SimpleFileLogger.LogError($"GetImageEmbeddingFromPathAsync: Ścieżka obrazu jest nieprawidłowa lub plik nie istnieje: '{imagePath}'", null);
                return null;
            }

            var payload = new { path = imagePath };
            string endpoint = $"{_baseAddress}/get_image_embedding";
            SimpleFileLogger.Log($"GetImageEmbeddingFromPathAsync: Przygotowano payload dla: {imagePath}. Adres docelowy: {endpoint}");

            try
            {
                SimpleFileLogger.Log($"GetImageEmbeddingFromPathAsync: Próba wysłania żądania POST dla: {imagePath} do {endpoint}");
                // Użyj przekazanego CancellationToken lub domyślnego (jeśli nie ma) z rozsądnym timeoutem
                var effectiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token);

                HttpResponseMessage response = await _httpClient.PostAsJsonAsync(endpoint, payload, effectiveCts.Token);
                SimpleFileLogger.Log($"GetImageEmbeddingFromPathAsync: Otrzymano odpowiedź dla: {imagePath}. Status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: effectiveCts.Token);
                    if (result?.embedding != null && result.embedding.Any())
                    {
                        SimpleFileLogger.Log($"GetImageEmbeddingFromPathAsync: Pomyślnie uzyskano embedding dla: {imagePath}, długość: {result.embedding.Count}");
                        return result.embedding.ToArray();
                    }
                    else
                    {
                        string errorContent = await response.Content.ReadAsStringAsync(effectiveCts.Token);
                        string detailMessage = result?.error ?? errorContent;
                        SimpleFileLogger.LogError($"GetImageEmbeddingFromPathAsync: Odpowiedź serwera OK, ale brak embeddingu lub błąd w odpowiedzi dla {imagePath}. Szczegóły: {detailMessage}", null);
                    }
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync(effectiveCts.Token);
                    SimpleFileLogger.LogError($"GetImageEmbeddingFromPathAsync: Błąd serwera ({response.StatusCode}) dla {imagePath}. Odpowiedź: {errorContent}", null);
                }
            }
            catch (HttpRequestException ex)
            {
                SimpleFileLogger.LogError($"GetImageEmbeddingFromPathAsync: HttpRequestException dla {imagePath} do {endpoint}: {ex.Message}. Sprawdź, czy serwer Pythona działa i nasłuchuje na {_baseAddress}", ex);
                _isServerConfirmedRunning = false;
            }
            catch (TaskCanceledException tex) when (cancellationToken.IsCancellationRequested)
            {
                SimpleFileLogger.LogWarning($"GetImageEmbeddingFromPathAsync: Operacja anulowana przez użytkownika dla {imagePath}.");
                throw; // Rzuć dalej, aby operacja nadrzędna mogła to obsłużyć
            }
            catch (TaskCanceledException tex) // Timeout
            {
                SimpleFileLogger.LogError($"GetImageEmbeddingFromPathAsync: Timeout podczas żądania embeddingu dla {imagePath} do {endpoint}: {tex.Message}", tex);
                _isServerConfirmedRunning = false;
            }
            catch (JsonException jsonEx)
            {
                SimpleFileLogger.LogError($"GetImageEmbeddingFromPathAsync: Błąd deserializacji JSON dla {imagePath} z {endpoint}: {jsonEx.Message}", jsonEx);
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"GetImageEmbeddingFromPathAsync: Nieoczekiwany błąd dla {imagePath} do {endpoint}: {ex.Message}", ex);
                _isServerConfirmedRunning = false;
            }
            return null;
        }

        public async Task<List<float[]>?> GetImageEmbeddingsBatchAsync(List<string> imagePaths, CancellationToken cancellationToken = default)
        {
            if (!_isServerConfirmedRunning)
            {
                SimpleFileLogger.LogWarning($"GetImageEmbeddingsBatchAsync: Połączenie z serwerem CLIP nie jest potwierdzone. Próba ponownego sprawdzenia.");
                if (!await EnsureServerConnectionAsync())
                {
                    SimpleFileLogger.LogError($"GetImageEmbeddingsBatchAsync: Serwer CLIP nadal nie jest dostępny. Nie można uzyskać embeddingów wsadowo.", null);
                    return null;
                }
            }

            if (imagePaths == null || !imagePaths.Any())
            {
                SimpleFileLogger.Log("GetImageEmbeddingsBatchAsync: Lista ścieżek obrazów jest pusta lub null.");
                return new List<float[]>();
            }

            var payload = new { paths = imagePaths };
            string endpoint = $"{_baseAddress}/get_image_embeddings_batch";
            SimpleFileLogger.Log($"GetImageEmbeddingsBatchAsync: Przygotowano payload dla {imagePaths.Count} obrazów. Adres docelowy: {endpoint}");

            try
            {
                var effectiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(5, imagePaths.Count * 0.5))).Token); // Dynamiczny timeout, min 5 minut

                HttpResponseMessage response = await _httpClient.PostAsJsonAsync(endpoint, payload, effectiveCts.Token);
                SimpleFileLogger.Log($"GetImageEmbeddingsBatchAsync: Otrzymano odpowiedź dla paczki {imagePaths.Count} obrazów. Status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<EmbeddingsBatchResponse>(cancellationToken: effectiveCts.Token);
                    if (result?.embeddings != null && result.embeddings.Count == imagePaths.Count)
                    {
                        SimpleFileLogger.Log($"GetImageEmbeddingsBatchAsync: Pomyślnie uzyskano {result.embeddings.Count} embeddingów.");
                        return result.embeddings.Select(e => e.ToArray()).ToList();
                    }
                    else
                    {
                        string errorContent = await response.Content.ReadAsStringAsync(effectiveCts.Token);
                        SimpleFileLogger.LogError($"GetImageEmbeddingsBatchAsync: Odpowiedź serwera OK, ale liczba embeddingów ({result?.embeddings?.Count}) niezgodna z liczbą żądanych ({imagePaths.Count}) lub błąd. Szczegóły: {errorContent}", null);
                    }
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync(effectiveCts.Token);
                    SimpleFileLogger.LogError($"GetImageEmbeddingsBatchAsync: Błąd serwera ({response.StatusCode}) dla paczki. Odpowiedź: {errorContent}", null);
                }
            }
            catch (HttpRequestException ex)
            {
                SimpleFileLogger.LogError($"GetImageEmbeddingsBatchAsync: HttpRequestException dla paczki do {endpoint}: {ex.Message}.", ex);
                _isServerConfirmedRunning = false;
            }
            catch (TaskCanceledException tex) when (cancellationToken.IsCancellationRequested)
            {
                SimpleFileLogger.LogWarning($"GetImageEmbeddingsBatchAsync: Operacja anulowana przez użytkownika dla paczki.");
                throw;
            }
            catch (TaskCanceledException tex) // Timeout
            {
                SimpleFileLogger.LogError($"GetImageEmbeddingsBatchAsync: Timeout podczas żądania embeddingów wsadowo do {endpoint}: {tex.Message}", tex);
                _isServerConfirmedRunning = false;
            }
            catch (JsonException jsonEx)
            {
                SimpleFileLogger.LogError($"GetImageEmbeddingsBatchAsync: Błąd deserializacji JSON dla paczki z {endpoint}: {jsonEx.Message}", jsonEx);
            }
            catch (Exception ex)
            {
                SimpleFileLogger.LogError($"GetImageEmbeddingsBatchAsync: Nieoczekiwany błąd dla paczki do {endpoint}: {ex.Message}", ex);
                _isServerConfirmedRunning = false;
            }
            return null;
        }


        public void Dispose()
        {
            SimpleFileLogger.LogHighLevelInfo("ClipServiceHttpClient.Dispose: Zwalnianie zasobów HttpClient.");
            _httpClient?.Dispose();
            GC.SuppressFinalize(this);
        }

        private class EmbeddingResponse
        {
            public List<float>? embedding { get; set; }
            public string? error { get; set; } // Dodane na wypadek, gdyby serwer zwracał błąd w ciele JSON
        }

        private class EmbeddingsBatchResponse
        {
            public List<List<float>>? embeddings { get; set; }
            public string? error { get; set; }
        }
    }
}