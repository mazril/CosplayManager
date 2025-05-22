# clip_server.py
import uvicorn
from fastapi import FastAPI, HTTPException, File, UploadFile, Form, Body
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from typing import List, Union, Optional, Dict, Any
import numpy as np
import os
import base64
from io import BytesIO
from PIL import Image
import logging
import pathlib
import time # Dla mechanizmu blokady i oczekiwania

# --- Konfiguracja Logowania ---
logger = logging.getLogger("clip_server")
logger.setLevel(logging.DEBUG)

if not logging.getLogger().handlers:
    ch = logging.StreamHandler()
    ch.setLevel(logging.DEBUG)
    formatter = logging.Formatter('%(asctime)s - %(name)s - PID:%(process)d - %(levelname)s - %(message)s') # Dodano PID do logów
    ch.setFormatter(formatter)
    logging.getLogger().addHandler(ch)
    logging.getLogger().setLevel(logging.DEBUG)
else: # Jeśli Uvicorn już skonfigurował handlery, zmodyfikuj ich formatter
    for handler in logging.getLogger().handlers:
        current_formatter = handler.getFormatter()
        if current_formatter and isinstance(current_formatter, logging.Formatter):
            # Zachowaj oryginalny format, ale dodaj PID
            fmt_str = current_formatter._fmt.replace('%(levelname)s - %(message)s', 'PID:%(process)d - %(levelname)s - %(message)s')
            new_formatter = logging.Formatter(fmt_str, datefmt=current_formatter.datefmt)
            handler.setFormatter(new_formatter)
        else: # Ustaw domyślny, jeśli nie ma formattera lub jest nieznany
            default_formatter = logging.Formatter('%(asctime)s - PID:%(process)d - %(levelname)s - %(message)s')
            handler.setFormatter(default_formatter)
# --- Koniec Konfiguracji Logowania ---

import onnxruntime
from transformers import AutoProcessor
from optimum.onnxruntime import ORTModelForFeatureExtraction
from huggingface_hub import snapshot_download

# --- Konfiguracja Ścieżek Modeli ---
SCRIPT_DIR = pathlib.Path(__file__).parent.resolve()
LOCAL_MODELS_ROOT_DIR = SCRIPT_DIR / "models"
LOCAL_MODELS_ROOT_DIR.mkdir(parents=True, exist_ok=True)
logger.info(f"Główny lokalny folder modeli: {LOCAL_MODELS_ROOT_DIR}")

# Plik blokady dla synchronizacji eksportu między procesami
EXPORT_LOCK_FILE = LOCAL_MODELS_ROOT_DIR / ".export_lock"
# --- Koniec Konfiguracji Ścieżek Modeli ---

class CLIPImageEmbedder:
    def __init__(self, model_id: str = "laion/CLIP-ViT-H-14-laion2B-s32B-b79K", device: str = "cuda"):
        logger.info(f"Inicjalizacja CLIPImageEmbedder z modelem '{model_id}' na '{device}'...")
        self.model_id = model_id
        self.requested_device = device
        self.effective_device = "cpu"
        self.processor = None
        self.model_ort_instance = None
        self.ort_session = None
        self.output_names = None

        safe_model_id_for_path = model_id.replace("/", "_--_")
        self.specific_local_model_path = LOCAL_MODELS_ROOT_DIR / safe_model_id_for_path
        logger.info(f"Lokalna ścieżka modelu: {self.specific_local_model_path}")

        # Plik znacznika wskazujący na pomyślny eksport ONNX do self.specific_local_model_path
        onnx_export_success_marker = self.specific_local_model_path / "_SUCCESSFUL_ONNX_EXPORT"

        # --- Krok 1: Pobierz pliki źródłowe modelu (np. PyTorch), jeśli folder modelu nie istnieje lub jest pusty ---
        if not self.specific_local_model_path.exists() or not any(self.specific_local_model_path.iterdir()):
            logger.info(f"Folder '{self.specific_local_model_path}' pusty/nie istnieje. Pobieranie repo '{model_id}'...")
            try:
                snapshot_download(repo_id=model_id, local_dir=self.specific_local_model_path, local_dir_use_symlinks=False)
                logger.info(f"Repo '{model_id}' pobrane do '{self.specific_local_model_path}'.")
            except Exception as e:
                logger.error(f"Błąd snapshot_download dla '{model_id}': {e}", exc_info=True)
                raise
        else:
            logger.info(f"Folder '{self.specific_local_model_path}' istnieje.")

        # --- Krok 2: Załaduj Procesor ---
        try:
            self.processor = AutoProcessor.from_pretrained(self.specific_local_model_path, local_files_only=True)
            logger.info(f"Procesor dla '{model_id}' załadowany z '{self.specific_local_model_path}'.")
        except Exception:
            logger.warning(f"Nie udało się załadować procesora lokalnie z '{self.specific_local_model_path}'. Próba z huba '{model_id}'.")
            try:
                self.processor = AutoProcessor.from_pretrained(model_id) # Fallback
                logger.info(f"Procesor dla '{model_id}' załadowany z huba (fallback).")
            except Exception as e_hub_proc:
                logger.error(f"Krytyczny błąd ładowania procesora dla '{model_id}': {e_hub_proc}", exc_info=True)
                raise

        # --- Krok 3: Sprawdź znacznik. Jeśli go nie ma, przeprowadź zsynchronizowany eksport ONNX ---
        if not onnx_export_success_marker.exists():
            logger.info(f"Znacznik udanego eksportu ONNX ('{onnx_export_success_marker.name}') nie znaleziony.")
            acquired_lock = False
            lock_fd = -1 # Inicjalizacja deskryptora pliku
            try:
                logger.info(f"Próba uzyskania blokady eksportu: {EXPORT_LOCK_FILE}")
                lock_fd = os.open(EXPORT_LOCK_FILE, os.O_CREAT | os.O_EXCL | os.O_RDWR)
                acquired_lock = True
                logger.info(f"Uzyskano blokadę eksportu. Rozpoczynanie eksportu ONNX dla '{self.model_id}'.")

                active_provider = self._determine_onnx_provider(onnxruntime.get_available_providers())
                
                logger.info(f"Eksportowanie '{self.model_id}' do ONNX z providerem '{active_provider}' (źródło z huba/cache)...")
                model_to_export = ORTModelForFeatureExtraction.from_pretrained(
                    self.model_id, 
                    provider=active_provider,
                    export=True,
                    use_io_binding=False
                )
                logger.info(f"Model '{self.model_id}' przekonwertowany do ONNX w pamięci.")
                logger.info(f"Zapisywanie wyeksportowanego modelu ONNX do '{self.specific_local_model_path}'...")
                model_to_export.save_pretrained(self.specific_local_model_path)
                logger.info(f"Wyeksportowany model ONNX pomyślnie zapisany w '{self.specific_local_model_path}'.")
                
                with open(onnx_export_success_marker, "w") as f:
                    f.write(f"Successfully exported and saved at {time.ctime()} by PID {os.getpid()}")
                logger.info(f"Utworzono znacznik udanego eksportu: {onnx_export_success_marker}")
                del model_to_export

            except FileExistsError: # Dotyczy EXPORT_LOCK_FILE
                logger.info(f"Inny proces już eksportuje model (plik blokady {EXPORT_LOCK_FILE} istnieje). Oczekiwanie...")
                wait_time = 0
                max_wait_time = 240 # Zwiększony czas oczekiwania do 4 minut
                sleep_interval = 5
                while not onnx_export_success_marker.exists() and wait_time < max_wait_time:
                    time.sleep(sleep_interval)
                    wait_time += sleep_interval
                    logger.info(f"Oczekuję na plik '{onnx_export_success_marker.name}'... ({wait_time}/{max_wait_time}s)")
                
                if not onnx_export_success_marker.exists():
                    logger.error(f"Przekroczono czas oczekiwania na eksport przez inny proces lub eksport się nie powiódł (brak znacznika).")
                    if EXPORT_LOCK_FILE.exists():
                        logger.warning(f"Plik blokady {EXPORT_LOCK_FILE} nadal istnieje. Może to oznaczać problem z procesem eksportującym.")
                    raise RuntimeError(f"Timeout/failure waiting for ONNX export by another process for {self.specific_local_model_path}")
                logger.info(f"Znacznik eksportu '{onnx_export_success_marker.name}' znaleziony. Zakładam, że eksport zakończony.")
            
            except Exception as e_export:
                logger.error(f"Błąd podczas eksportu i zapisu modelu ONNX: {e_export}", exc_info=True)
                # Logika fallback na CPU, jeśli eksport CUDA zawiódł (tylko jeśli ten proces zdobył blokadę)
                if acquired_lock and hasattr(self, 'active_provider') and self.active_provider == "CUDAExecutionProvider" and "CPUExecutionProvider" in onnxruntime.get_available_providers():
                    logger.warning(f"Eksport z CUDA nie powiódł się. Próba eksportu z CPUExecutionProvider...")
                    try:
                        cpu_provider = "CPUExecutionProvider"
                        model_to_export_cpu = ORTModelForFeatureExtraction.from_pretrained(
                            self.model_id, provider=cpu_provider, export=True, use_io_binding=False
                        )
                        model_to_export_cpu.save_pretrained(self.specific_local_model_path)
                        logger.info(f"Model pomyślnie wyeksportowany do ONNX (CPU fallback) i zapisany w '{self.specific_local_model_path}'.")
                        with open(onnx_export_success_marker, "w") as f:
                            f.write(f"Successfully exported (CPU fallback) and saved at {time.ctime()} by PID {os.getpid()}")
                        logger.info(f"Utworzono znacznik udanego eksportu (CPU fallback): {onnx_export_success_marker}")
                        del model_to_export_cpu
                    except Exception as e_export_cpu:
                        logger.error(f"Fallback eksportu na CPU również zawiódł: {e_export_cpu}", exc_info=True)
                        # Nie rzucamy tutaj błędu, jeśli główny eksport nie powiódł się, bo blokada musi być zwolniona
                # Jeśli nie udało się utworzyć znacznika, mimo wszystko rzuć błąd, aby proces wiedział, że coś poszło nie tak
                if not onnx_export_success_marker.exists():
                    raise RuntimeError(f"Eksport modelu {self.model_id} do ONNX nie powiódł się i nie utworzono znacznika sukcesu.")

            finally:
                if acquired_lock and lock_fd != -1: # Sprawdź czy deskryptor jest poprawny
                    try:
                        os.close(lock_fd)
                        os.remove(EXPORT_LOCK_FILE)
                        logger.info(f"Zwolniono blokadę eksportu.")
                    except OSError as e_lock:
                        logger.error(f"Błąd podczas zwalniania blokady eksportu: {e_lock}")
        else:
            logger.info(f"Znacznik '{onnx_export_success_marker.name}' już istnieje. Zakładam, że model ONNX jest gotowy w '{self.specific_local_model_path}'.")

        # --- Krok 4: Załaduj model ONNX z lokalnej ścieżki ---
        # Provider powinien być już ustalony przez proces eksportujący lub przez _determine_onnx_provider
        final_provider = self._determine_onnx_provider(onnxruntime.get_available_providers())
        # Zapisz wybrany provider, aby ewentualny fallback go widział
        self.active_provider = final_provider 
        logger.info(f"Ładowanie finalnego modelu ONNX z '{self.specific_local_model_path}' providerem '{final_provider}'...")
        try:
            self.model_ort_instance = ORTModelForFeatureExtraction.from_pretrained(
                self.specific_local_model_path,
                provider=final_provider,
                local_files_only=True,
                use_io_binding=False
            )
            self.ort_session = self.model_ort_instance.model
            self.output_names = [output.name for output in self.ort_session.get_outputs()]
            
            session_providers = self.ort_session.get_providers()
            logger.info(f"Sesja ONNX załadowana z '{self.specific_local_model_path}', providery: {session_providers}")

            if "CUDAExecutionProvider" in session_providers: self.effective_device = "cuda"
            elif "CPUExecutionProvider" in session_providers: self.effective_device = "cpu"
            else: self.effective_device = "cpu"; logger.warning(f"Dziwny stan providerów, zakładam CPU.")
            logger.info(f"Model załadowany, efektywne urządzenie: {self.effective_device}")

        except Exception as e_load_final:
            logger.error(f"Krytyczny błąd ładowania modelu ONNX z '{self.specific_local_model_path}': {e_load_final}", exc_info=True)
            if final_provider == "CUDAExecutionProvider" and "CPUExecutionProvider" in onnxruntime.get_available_providers():
                logger.warning(f"Ładowanie CUDA nie powiodło się, próba CPU dla istniejących plików ONNX...")
                try:
                    self.model_ort_instance = ORTModelForFeatureExtraction.from_pretrained(
                        self.specific_local_model_path, provider="CPUExecutionProvider", local_files_only=True, use_io_binding=False
                    )
                    self.ort_session = self.model_ort_instance.model
                    self.output_names = [output.name for output in self.ort_session.get_outputs()]
                    self.effective_device = "cpu"
                    logger.info(f"Model ONNX załadowany z CPU (fallback).")
                except Exception as e_cpu_fallback:
                    logger.error(f"Fallback ładowania ONNX na CPU również zawiódł: {e_cpu_fallback}", exc_info=True)
                    raise # Rzuć błąd, jeśli nawet CPU zawiedzie
            else:
                raise


        if not hasattr(self.processor, 'tokenizer') or self.processor.tokenizer is None:
            logger.error(f"Krytyczny błąd: Załadowany procesor nie posiada tokenizera.")
            
        if self.ort_session is None:
            logger.critical(f"Nie udało się zainicjalizować sesji ONNX!")
            raise RuntimeError("Inicjalizacja sesji ONNX nie powiodła się.")

        logger.info(f"Zakończono pomyślnie inicjalizację CLIPImageEmbedder dla '{model_id}'.")


    def _determine_onnx_provider(self, available_providers_list: List[str]) -> str:
        provider = "CPUExecutionProvider" 
        if self.requested_device == "cuda":
            if "CUDAExecutionProvider" in available_providers_list:
                provider = "CUDAExecutionProvider"
            else:
                logger.warning(f"Żądano CUDA, ale niedostępny. Używam CPU.")
        elif self.requested_device != "cpu":
             logger.warning(f"Nieznane urządzenie '{self.requested_device}'. Próba CUDA, potem CPU.")
             if "CUDAExecutionProvider" in available_providers_list: provider = "CUDAExecutionProvider"
        
        if provider not in available_providers_list: # Jeśli wybrany (np. CUDA) nie jest dostępny, spróbuj CPU
            logger.error(f"Wybrany provider '{provider}' nie jest na liście dostępnych: {available_providers_list}. Używam CPUExecutionProvider jako fallback.")
            if "CPUExecutionProvider" in available_providers_list: return "CPUExecutionProvider"
            raise RuntimeError(f"Krytyczny błąd: CPUExecutionProvider nie jest dostępny! Dostępne: {available_providers_list}")
        logger.info(f"Wybrany provider ONNX do użycia: {provider}")
        return provider

    def _load_image(self, image_input: Union[str, Image.Image, bytes]) -> Image.Image:
        if isinstance(image_input, str): 
            if not os.path.exists(image_input):
                logger.error(f"Plik obrazu nie znaleziony: {image_input}")
                raise FileNotFoundError(f"Plik obrazu nie znaleziony: {image_input}")
            image = Image.open(image_input).convert("RGB")
        elif isinstance(image_input, Image.Image): 
            image = image_input.convert("RGB")
        elif isinstance(image_input, bytes): 
            image = Image.open(BytesIO(image_input)).convert("RGB")
        else:
            logger.error(f"Nieprawidłowy typ wejścia dla obrazu: {type(image_input)}")
            raise ValueError("Wejście musi być ścieżką do pliku, obiektem PIL.Image lub bajtami obrazu.")
        return image

    def _prepare_model_inputs(self, inputs_to_filter: Dict[str, Any]) -> Dict[str, Any]:
        try:
            model_expected_inputs = [inp.name for inp in self.ort_session.get_inputs()]
        except Exception as e_inputs:
            logger.error(f"Nie można automatycznie ustalić nazw wejść modelu ONNX: {e_inputs}", exc_info=True)
            return inputs_to_filter 
        
        filtered_inputs = {key: val for key, val in inputs_to_filter.items() if key in model_expected_inputs}
        return filtered_inputs

    def _get_dummy_text_inputs(self, batch_size: int) -> Dict[str, np.ndarray]:
        if not hasattr(self.processor, 'tokenizer') or self.processor.tokenizer is None:
            logger.warning("Tokenizer nie jest dostępny w procesorze. Zwracam puste dummy inputs, co może prowadzić do błędów.")
            return {} 

        dummy_texts = [""] * batch_size 
        max_len = getattr(getattr(self.processor, 'tokenizer', None), 'model_max_length', 77)

        processed_text_inputs = self.processor(
            text=dummy_texts,
            return_tensors="np",
            padding="max_length", 
            max_length=max_len,
            truncation=True 
        )
        return {
            'input_ids': processed_text_inputs['input_ids'],
            'attention_mask': processed_text_inputs['attention_mask']
        }

    def get_image_embedding(self, image_input: Union[str, Image.Image, bytes]) -> np.ndarray:
        image = self._load_image(image_input)
        processed_image_inputs = self.processor(images=[image], return_tensors="np", padding=True)
        dummy_text_inputs_for_batch = self._get_dummy_text_inputs(batch_size=1)
        combined_inputs = {
            'pixel_values': processed_image_inputs['pixel_values'],
            **dummy_text_inputs_for_batch 
        }
        model_inputs_filtered = self._prepare_model_inputs(combined_inputs)
        
        try:
            raw_outputs = self.ort_session.run(None, model_inputs_filtered) 
            output_dict = dict(zip(self.output_names, raw_outputs))
            target_output_name = 'image_embeds' 
            
            if target_output_name in output_dict:
                embedding = output_dict[target_output_name]
                if embedding.ndim > 1 and embedding.shape[0] == 1: 
                    embedding = embedding[0] 
            elif 'last_hidden_state' in output_dict and len(self.output_names) == 1: 
                 logger.warning(f"Nie znaleziono '{target_output_name}', próbuję 'last_hidden_state'. To może nie być właściwe osadzenie CLIP.")
                 embedding = output_dict['last_hidden_state'][:, 0, :] 
                 if embedding.ndim > 1 and embedding.shape[0] == 1: embedding = embedding[0]
            else:
                logger.error(f"Nie udało się znaleźć wyjścia '{target_output_name}' ani 'last_hidden_state' w wynikach modelu. Dostępne wyjścia: {self.output_names}")
                raise RuntimeError(f"Nie można uzyskać osadzeń obrazu z modelu. Dostępne wyjścia: {self.output_names}")
        except Exception as e:
            logger.error(f"Błąd podczas inferencji modelu dla obrazu: {e}", exc_info=True)
            raise
        return embedding

    def get_image_embeddings_batch(self, image_inputs: List[Union[str, Image.Image, bytes]]) -> np.ndarray:
        if not image_inputs: return np.array([])
        batch_size = len(image_inputs)
        images = [self._load_image(img_input) for img_input in image_inputs]
        processed_image_inputs = self.processor(images=images, return_tensors="np", padding=True)
        dummy_text_inputs_for_batch = self._get_dummy_text_inputs(batch_size=batch_size)
        combined_inputs = {
            'pixel_values': processed_image_inputs['pixel_values'],
            **dummy_text_inputs_for_batch
        }
        model_inputs_filtered = self._prepare_model_inputs(combined_inputs)

        try:
            raw_outputs = self.ort_session.run(None, model_inputs_filtered)
            output_dict = dict(zip(self.output_names, raw_outputs))
            target_output_name = 'image_embeds'
            
            if target_output_name in output_dict:
                embeddings = output_dict[target_output_name]
            elif 'last_hidden_state' in output_dict and len(self.output_names) == 1:
                 logger.warning(f"Nie znaleziono '{target_output_name}', próbuję 'last_hidden_state' dla batcha. To może nie być właściwe osadzenie CLIP.")
                 embeddings = output_dict['last_hidden_state'][:, 0, :] 
            else:
                logger.error(f"Nie udało się znaleźć wyjścia '{target_output_name}' ani 'last_hidden_state' w wynikach modelu (batch). Dostępne wyjścia: {self.output_names}")
                raise RuntimeError(f"Nie można uzyskać osadzeń obrazu z modelu (batch). Dostępne wyjścia: {self.output_names}")
        except Exception as e:
            logger.error(f"Błąd podczas inferencji modelu dla partii obrazów: {e}", exc_info=True)
            raise
        return embeddings
    
    def get_text_embedding(self, text: str) -> np.ndarray:
        # ... (bez zmian)
        model_input_names = []
        try: model_input_names = [inp.name for inp in self.ort_session.get_inputs()]
        except Exception as e_inputs: raise RuntimeError(f"Nie można ustalić nazw wejść modelu ONNX dla tekstu: {e_inputs}")

        if 'input_ids' not in model_input_names: 
             raise NotImplementedError("Wektoryzacja tekstu nie jest obsługiwana (brak 'input_ids').")
        
        processed_inputs = self.processor(text=[text], return_tensors="np", padding=True)
        model_inputs_filtered = self._prepare_model_inputs(processed_inputs) 
        
        try:
            raw_outputs = self.ort_session.run(None, model_inputs_filtered)
            output_dict = dict(zip(self.output_names, raw_outputs))
            target_output_name = 'text_embeds' 

            if target_output_name in output_dict:
                embedding = output_dict[target_output_name]
                if embedding.ndim > 1 and embedding.shape[0] == 1: embedding = embedding[0]
            elif 'pooler_output' in output_dict: 
                 logger.warning(f"Nie znaleziono '{target_output_name}', próbuję 'pooler_output'.")
                 embedding = output_dict['pooler_output']
                 if embedding.ndim > 1 and embedding.shape[0] == 1: embedding = embedding[0]
            elif 'last_hidden_state' in output_dict: 
                 logger.warning(f"Nie znaleziono '{target_output_name}' ani 'pooler_output', próbuję 'last_hidden_state'[:,0,:].")
                 embedding = output_dict['last_hidden_state'][:, 0, :] 
                 if embedding.ndim > 1 and embedding.shape[0] == 1: embedding = embedding[0]
            else:
                logger.error(f"Nie udało się znaleźć wyjścia '{target_output_name}', 'pooler_output', ani 'last_hidden_state' w wynikach modelu. Dostępne wyjścia: {self.output_names}")
                raise RuntimeError(f"Nie można uzyskać osadzeń tekstu. Dostępne wyjścia: {self.output_names}")
        except Exception as e:
            logger.error(f"Błąd podczas inferencji modelu dla tekstu: {e}", exc_info=True)
            raise
        return embedding

    def get_text_embeddings_batch(self, texts: List[str]) -> np.ndarray:
        # ... (bez zmian)
        if not texts: return np.array([])
        try: model_input_names = [inp.name for inp in self.ort_session.get_inputs()]
        except Exception as e_inputs: raise RuntimeError(f"Nie można ustalić nazw wejść modelu ONNX dla tekstu: {e_inputs}")
        
        if 'input_ids' not in model_input_names:
             raise NotImplementedError("Wektoryzacja tekstu nie jest obsługiwana (brak 'input_ids').")
        
        processed_inputs = self.processor(text=texts, return_tensors="np", padding=True)
        model_inputs_filtered = self._prepare_model_inputs(processed_inputs)

        try:
            raw_outputs = self.ort_session.run(None, model_inputs_filtered)
            output_dict = dict(zip(self.output_names, raw_outputs))
            target_output_name = 'text_embeds'

            if target_output_name in output_dict:
                embeddings = output_dict[target_output_name]
            elif 'pooler_output' in output_dict:
                logger.warning(f"Nie znaleziono '{target_output_name}', próbuję 'pooler_output' dla batcha.")
                embeddings = output_dict['pooler_output']
            elif 'last_hidden_state' in output_dict:
                logger.warning(f"Nie znaleziono '{target_output_name}' ani 'pooler_output', próbuję 'last_hidden_state'[:,0,:] dla batcha.")
                embeddings = output_dict['last_hidden_state'][:, 0, :]
            else:
                logger.error(f"Nie udało się znaleźć wyjścia '{target_output_name}', 'pooler_output', ani 'last_hidden_state' w wynikach modelu (batch). Dostępne wyjścia: {self.output_names}")
                raise RuntimeError(f"Nie można uzyskać osadzeń tekstu (batch). Dostępne wyjścia: {self.output_names}")

        except Exception as e:
            logger.error(f"Błąd podczas inferencji modelu dla partii tekstów: {e}", exc_info=True)
            raise
        return embeddings

# --- FastAPI app setup, endpoints, startup_event ---
app = FastAPI()
embedder: Optional[CLIPImageEmbedder] = None

@app.on_event("startup")
async def startup_event():
    global embedder
    logger.info(f"Główny proces/worker (PID: {os.getpid()}): Uruchamianie serwera FastAPI, inicjalizacja CLIPImageEmbedder...")
    try:
        embedder = CLIPImageEmbedder()
        if embedder and embedder.model_ort_instance and embedder.ort_session:
             logger.info(f"CLIPImageEmbedder (PID: {os.getpid()}) zainicjalizowany pomyślnie. Używane urządzenie: {embedder.effective_device}")
        else:
             logger.critical(f"Nie udało się w pełni zainicjalizować embeddera (PID: {os.getpid()}).")
             embedder = None
    except Exception as e:
        logger.critical(f"Krytyczny błąd podczas inicjalizacji CLIPImageEmbedder na starcie (PID: {os.getpid()}): {e}", exc_info=True)
        embedder = None
    logger.info(f"Zakończono startup_event (PID: {os.getpid()}).")

# ... (reszta definicji modeli Pydantic i endpointów bez zmian)
class ImagePathInput(BaseModel):
    path: str = Field(..., example="C:\\Users\\Admin\\Pictures\\cosplay_image.jpg")

class ImagePathsInput(BaseModel):
    paths: List[str] = Field(..., example=["path/to/img1.jpg", "path/to/img2.png"])

class TextIn(BaseModel):
    text: str = Field(..., example="A beautiful cosplay photo.")

class TextsIn(BaseModel):
    texts: List[str] = Field(..., example=["cosplay", "character", "triss merigold"])

class EmbeddingResponse(BaseModel):
    embedding: List[float]

class EmbeddingsResponse(BaseModel):
    embeddings: List[List[float]]

class ErrorResponse(BaseModel):
    detail: str
    type: Optional[str] = None 

@app.post("/get_image_embedding", 
          response_model=EmbeddingResponse,
          responses={
              503: {"model": ErrorResponse, "description": "Embedder nie jest zainicjalizowany"}, 
              404: {"model": ErrorResponse, "description": "Obraz nie znaleziony"}, 
              500: {"model": ErrorResponse, "description": "Wewnętrzny błąd serwera"}
          })
async def get_image_embedding_endpoint(data: ImagePathInput = Body(...)):
    if not embedder or not embedder.ort_session: 
        logger.error("Żądanie /get_image_embedding, ale embedder nie jest w pełni zainicjalizowany.")
        raise HTTPException(status_code=503, detail="Embedder nie jest zainicjalizowany lub sesja ONNX nie została załadowana")
    try:
        # logger.info(f"Przetwarzanie obrazu ze ścieżki: {data.path}")
        embedding = embedder.get_image_embedding(data.path)
        return EmbeddingResponse(embedding=embedding.tolist())
    except FileNotFoundError:
        logger.warning(f"Nie znaleziono obrazu: {data.path}")
        raise HTTPException(status_code=404, detail=f"Obraz nie znaleziony: {data.path}")
    except Exception as e:
        logger.error(f"Wewnętrzny błąd serwera dla {data.path}: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Wewnętrzny błąd serwera: {type(e).__name__} - {str(e)}")

@app.post("/get_image_embedding_upload", response_model=EmbeddingResponse)
async def get_image_embedding_upload_endpoint(file: UploadFile = File(...)):
    if not embedder or not embedder.ort_session:
        raise HTTPException(status_code=503, detail="Embedder nie jest zainicjalizowany lub sesja ONNX nie została załadowana")
    try:
        # logger.info(f"Przetwarzanie załadowanego pliku: {file.filename}")
        image_bytes = await file.read()
        embedding = embedder.get_image_embedding(image_bytes)
        return EmbeddingResponse(embedding=embedding.tolist())
    except Exception as e:
        logger.error(f"Błąd przetwarzania załadowanego pliku {file.filename}: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Wewnętrzny błąd serwera: {type(e).__name__} - {str(e)}")

@app.post("/get_image_embeddings_batch", response_model=EmbeddingsResponse)
async def get_image_embeddings_batch_endpoint(data: ImagePathsInput = Body(...)):
    if not embedder or not embedder.ort_session:
        raise HTTPException(status_code=503, detail="Embedder nie jest zainicjalizowany lub sesja ONNX nie została załadowana")
    try:
        # logger.info(f"Przetwarzanie partii {len(data.paths)} obrazów.")
        embeddings = embedder.get_image_embeddings_batch(data.paths)
        return EmbeddingsResponse(embeddings=[emb.tolist() for emb in embeddings])
    except Exception as e: 
        logger.error(f"Błąd przetwarzania partii obrazów: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Wewnętrzny błąd serwera: {type(e).__name__} - {str(e)}")

@app.post("/get_text_embedding", response_model=EmbeddingResponse)
async def get_text_embedding_endpoint(data: TextIn = Body(...)):
    if not embedder or not embedder.ort_session: 
        raise HTTPException(status_code=503, detail="Embedder nie jest zainicjalizowany lub sesja ONNX nie została załadowana")
    try:
        embedding = embedder.get_text_embedding(data.text)
        return EmbeddingResponse(embedding=embedding.tolist())
    except NotImplementedError as e:
        raise HTTPException(status_code=501, detail=str(e)) 
    except Exception as e: 
        raise HTTPException(status_code=500, detail=f"Wewnętrzny błąd serwera: {type(e).__name__} - {str(e)}")

@app.post("/get_text_embeddings_batch", response_model=EmbeddingsResponse)
async def get_text_embeddings_batch_endpoint(data: TextsIn = Body(...)):
    if not embedder or not embedder.ort_session: 
        raise HTTPException(status_code=503, detail="Embedder nie jest zainicjalizowany lub sesja ONNX nie została załadowana")
    try:
        embeddings = embedder.get_text_embeddings_batch(data.texts)
        return EmbeddingsResponse(embeddings=[emb.tolist() for emb in embeddings])
    except NotImplementedError as e:
        raise HTTPException(status_code=501, detail=str(e))
    except Exception as e: 
        raise HTTPException(status_code=500, detail=f"Wewnętrzny błąd serwera: {type(e).__name__} - {str(e)}")

@app.get("/health")
async def health_check():
    initialized_fully = False
    current_device = "N/A"
    details = "Embedder nie został zainicjalizowany."

    if embedder:
        current_device = embedder.effective_device
        if embedder.model_ort_instance and embedder.processor and embedder.ort_session: 
            initialized_fully = True
            details = f"Embedder zainicjalizowany. Efektywne urządzenie: {current_device}."
        else:
            details = f"Embedder częściowo zainicjalizowany. Sprawdź logi. Efektywne urządzenie: {current_device}."
            
    status = "ok" if initialized_fully else "error"
    return {"status": status, "embedder_fully_initialized": initialized_fully, "effective_device": current_device, "details": details}


if __name__ == "__main__":
    logger.info("Startowanie serwera Uvicorn dla CLIP embeddings...")
    # UWAGA: Przy pierwszym uruchomieniu po zmianach (lub jeśli folder 'models' jest pusty),
    # uruchom z --reload LUB --workers 1, aby pozwolić na jednorazowy, zsynchronizowany eksport modelu.
    # Dopiero potem używaj --workers N (np. --workers 4) dla wielu procesów.
    uvicorn.run("clip_server:app", host="127.0.0.1", port=8008, log_level="info", reload=True)
    
    # Przykład dla wielu workerów (po udanym pierwszym eksporcie):
    # uvicorn.run("clip_server:app", host="127.0.0.1", port=8008, workers=4, log_level="info")