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
import logging # <--- UPEWNIJ SIĘ, ŻE TEN IMPORT JEST OBECNY I NA POCZĄTKU

# --- Konfiguracja Logowania ---
logger = logging.getLogger("clip_server") 
logger.setLevel(logging.DEBUG) 

# Upewniamy się, że handler (np. konsoli) również jest ustawiony na DEBUG
# Jeśli basicConfig nie był wywołany wcześniej lub chcemy nadpisać:
if not logging.getLogger().handlers: 
    ch = logging.StreamHandler()
    ch.setLevel(logging.DEBUG)
    formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
    ch.setFormatter(formatter)
    logging.getLogger().addHandler(ch) 
    logging.getLogger().setLevel(logging.DEBUG)
else: 
    for handler in logging.getLogger().handlers:
        # Bezpieczniej jest nie zmieniać poziomu handlerów skonfigurowanych przez uvicorn,
        # chyba że wiemy co robimy. Ustawienie poziomu loggera powinno wystarczyć.
        # handler.setLevel(logging.DEBUG) # Można odkomentować jeśli konieczne
        pass
# --- Koniec Konfiguracji Logowania ---


import onnxruntime 
from transformers import AutoProcessor
from optimum.onnxruntime import ORTModelForFeatureExtraction


class CLIPImageEmbedder:
    def __init__(self, model_id: str = "laion/CLIP-ViT-H-14-laion2B-s32B-b79K", device: str = "cuda"):
        logger.info(f"Inicjalizacja CLIPImageEmbedder z modelem '{model_id}' na żądanym urządzeniu '{device}'...")
        # ... reszta kodu ...
        self.model_id = model_id
        self.requested_device = device 
        self.effective_device = "cpu" 
        self.processor = None
        self.model_ort_instance = None 
        self.ort_session = None      
        self.output_names = None     

        try:
            self.processor = AutoProcessor.from_pretrained(self.model_id)
            logger.info(f"Procesor dla modelu {self.model_id} załadowany pomyślnie.")
        except Exception as e:
            logger.error(f"Błąd podczas ładowania procesora dla {self.model_id}: {e}", exc_info=True)
            raise

        available_providers = onnxruntime.get_available_providers()
        provider_to_try = None
        
        if self.requested_device == "cuda":
            if "CUDAExecutionProvider" in available_providers:
                provider_to_try = "CUDAExecutionProvider"
                logger.info("CUDAExecutionProvider jest dostępny. Próba użycia GPU.")
            else:
                logger.warning("Żądano CUDA, ale CUDAExecutionProvider nie jest dostępny. Próba użycia CPU.")
                provider_to_try = "CPUExecutionProvider"
                if provider_to_try not in available_providers:
                     logger.critical("CPUExecutionProvider również nie jest dostępny!")
                     raise RuntimeError("Brak dostępnych providerów ONNX Runtime (CPU lub CUDA).")
        elif self.requested_device == "cpu":
            if "CPUExecutionProvider" in available_providers:
                provider_to_try = "CPUExecutionProvider"
                logger.info("Żądano CPU. Próba użycia CPUExecutionProvider.")
            else:
                logger.critical("Żądano CPU, ale CPUExecutionProvider nie jest dostępny!")
                raise RuntimeError("CPUExecutionProvider nie jest dostępny.")
        else:
            logger.error(f"Nieznane żądane urządzenie: {self.requested_device}. Używam domyślnej logiki wyboru providera.")
            if "CUDAExecutionProvider" in available_providers: provider_to_try = "CUDAExecutionProvider"
            elif "CPUExecutionProvider" in available_providers: provider_to_try = "CPUExecutionProvider"
            else: raise RuntimeError("Brak dostępnych providerów ONNX Runtime.")

        logger.info(f"Próba załadowania/eksportu modelu '{self.model_id}' z providerem: {provider_to_try}...")
        try:
            self.model_ort_instance = ORTModelForFeatureExtraction.from_pretrained(
                self.model_id,
                provider=provider_to_try,
                export=True,
                use_io_binding=False 
            )
            self.ort_session = self.model_ort_instance.model 
            self.output_names = [output.name for output in self.ort_session.get_outputs()] 
            
            session_providers = self.ort_session.get_providers() 
            logger.info(f"Sesja ONNX Runtime skonfigurowana z providerami: {session_providers}")

            if "CUDAExecutionProvider" in session_providers:
                self.effective_device = "cuda"
                logger.info(f"Model pomyślnie załadowany/wyeksportowany. Efektywny provider: CUDAExecutionProvider. Urządzenie ustawione na 'cuda'.")
            elif "CPUExecutionProvider" in session_providers:
                self.effective_device = "cpu"
                logger.info(f"Model pomyślnie załadowany/wyeksportowany. Efektywny provider: CPUExecutionProvider. Urządzenie ustawione na 'cpu'.")
            else:
                self.effective_device = "cpu" 
                logger.warning(f"Model załadowany, ale detekcja providera niejasna (użyte: {session_providers}). Zakładam CPU.")

        except Exception as e:
            logger.error(f"Nie udało się załadować/wyeksportować modelu z providerem {provider_to_try}: {e}", exc_info=True)
            if provider_to_try == "CUDAExecutionProvider" and "CPUExecutionProvider" in available_providers:
                logger.warning("Próba z CUDA nie powiodła się. Próba fallbacku na CPUExecutionProvider...")
                try:
                    self.model_ort_instance = ORTModelForFeatureExtraction.from_pretrained(
                        self.model_id,
                        provider="CPUExecutionProvider",
                        export=True,
                        use_io_binding=False 
                    )
                    self.ort_session = self.model_ort_instance.model 
                    self.output_names = [output.name for output in self.ort_session.get_outputs()]

                    session_providers = self.ort_session.get_providers() 
                    logger.info(f"Sesja ONNX Runtime (fallback CPU) skonfigurowana z providerami: {session_providers}")
                    if "CPUExecutionProvider" in session_providers:
                        self.effective_device = "cpu"
                        logger.info("Model pomyślnie załadowany/wyeksportowany z CPUExecutionProvider (fallback).")
                    else: 
                        self.effective_device = "cpu"
                        logger.error("Fallback na CPU wydawał się udany, ale CPUExecutionProvider nie jest na liście aktywnych providerów!")
                except Exception as e_cpu:
                    logger.error(f"Fallback na CPU (ładowanie/eksport) również zawiódł: {e_cpu}", exc_info=True)
                    raise e_cpu 
            else: 
                raise e 

        if not hasattr(self.processor, 'tokenizer') or self.processor.tokenizer is None:
            logger.error("Krytyczny błąd: Załadowany procesor nie posiada tokenizera. Nie można utworzyć zaślepkowych danych tekstowych.")

        if self.ort_session is None: 
            logger.critical("Nie udało się zainicjalizować sesji ONNX (self.ort_session is None)!")
            raise RuntimeError("Inicjalizacja sesji ONNX nie powiodła się.")

        logger.info(f"Zakończono próbę inicjalizacji CLIPImageEmbedder. Model: {self.model_id}, Efektywne Urządzenie: {self.effective_device}")


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
            logger.debug(f"DEBUG _prepare_model_inputs: model_expected_inputs = {model_expected_inputs}")
        except Exception as e_inputs:
            logger.error(f"Nie można automatycznie ustalić nazw wejść modelu ONNX: {e_inputs}", exc_info=True)
            return inputs_to_filter 
        
        filtered_inputs = {key: val for key, val in inputs_to_filter.items() if key in model_expected_inputs}
        logger.debug(f"DEBUG _prepare_model_inputs: przefiltrowane klucze wejść = {list(filtered_inputs.keys())}")
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
        
        logger.debug("--- DEBUG: Szczegóły wejść do self.ort_session.run() (get_image_embedding) ---")
        if model_inputs_filtered is None: logger.debug("DEBUG: model_inputs_filtered jest None!")
        else:
            logger.debug(f"DEBUG: Klucze w model_inputs_filtered: {list(model_inputs_filtered.keys())}")
            for key, value in model_inputs_filtered.items():
                value_type = type(value); shape_info = "N/A"; dtype_info = "N/A"
                if isinstance(value, np.ndarray): shape_info = value.shape; dtype_info = value.dtype
                elif hasattr(value, 'shape'): shape_info = value.shape;  
                if hasattr(value, 'dtype'): dtype_info = value.dtype
                logger.debug(f"DEBUG: Klucz: '{key}', Typ: {value_type}, Kształt: {shape_info}, Dtype: {dtype_info}")
        logger.debug(f"DEBUG: Nazwy wyjść dla sesji: {self.output_names}")
        logger.debug("--- KONIEC DEBUG ---")

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
            logger.error(f"Błąd podczas inferencji modelu dla obrazu (bezpośrednie wywołanie sesji): {e}", exc_info=True)
            expected_input_names_str = "N/A"
            try: expected_input_names_str = str([inp.name for inp in self.ort_session.get_inputs()])
            except: pass
            logger.error(f"Oczekiwane wejścia modelu: {expected_input_names_str}")
            logger.error(f"Dostarczone wejścia przez procesor (obraz): {list(processed_image_inputs.keys())}")
            logger.error(f"Dostarczone wejścia przez procesor (dummy text): {list(dummy_text_inputs_for_batch.keys())}")
            logger.error(f"Przekazane wejścia do modelu po _prepare_model_inputs (klucze): {list(model_inputs_filtered.keys())}")
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

        logger.debug("--- DEBUG: Szczegóły wejść do self.ort_session.run() (get_image_embeddings_batch) ---")
        if model_inputs_filtered is None: logger.debug("DEBUG: model_inputs_filtered jest None!")
        else:
            logger.debug(f"DEBUG: Klucze w model_inputs_filtered: {list(model_inputs_filtered.keys())}")
            for key, value in model_inputs_filtered.items():
                value_type = type(value); shape_info = "N/A"; dtype_info = "N/A"
                if isinstance(value, np.ndarray): shape_info = value.shape; dtype_info = value.dtype
                logger.debug(f"DEBUG: Klucz: '{key}', Typ: {value_type}, Kształt: {shape_info}, Dtype: {dtype_info}")
        logger.debug(f"DEBUG: Nazwy wyjść dla sesji: {self.output_names}")
        logger.debug("--- KONIEC DEBUG ---")

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
            logger.error(f"Błąd podczas inferencji modelu dla partii obrazów (bezpośrednie wywołanie sesji): {e}", exc_info=True)
            raise
        return embeddings
    
    def get_text_embedding(self, text: str) -> np.ndarray:
        model_input_names = []
        try: model_input_names = [inp.name for inp in self.ort_session.get_inputs()]
        except Exception as e_inputs: raise RuntimeError(f"Nie można ustalić nazw wejść modelu ONNX dla tekstu: {e_inputs}")

        if 'input_ids' not in model_input_names: 
             raise NotImplementedError("Wektoryzacja tekstu nie jest obsługiwana (brak 'input_ids').")
        
        processed_inputs = self.processor(text=[text], return_tensors="np", padding=True)
        model_inputs_filtered = self._prepare_model_inputs(processed_inputs) 
        
        logger.debug(f"--- DEBUG: Szczegóły wejść do self.ort_session.run() (get_text_embedding) ---")
        if model_inputs_filtered is None: logger.debug("DEBUG: model_inputs_filtered jest None!")
        else:
            logger.debug(f"DEBUG: Klucze w model_inputs_filtered: {list(model_inputs_filtered.keys())}")
        logger.debug(f"DEBUG: Nazwy wyjść dla sesji: {self.output_names}")
        logger.debug("--- KONIEC DEBUG ---")
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
        if not texts: return np.array([])
        try: model_input_names = [inp.name for inp in self.ort_session.get_inputs()]
        except Exception as e_inputs: raise RuntimeError(f"Nie można ustalić nazw wejść modelu ONNX dla tekstu: {e_inputs}")
        
        if 'input_ids' not in model_input_names:
             raise NotImplementedError("Wektoryzacja tekstu nie jest obsługiwana (brak 'input_ids').")
        
        processed_inputs = self.processor(text=texts, return_tensors="np", padding=True)
        model_inputs_filtered = self._prepare_model_inputs(processed_inputs)

        logger.debug(f"--- DEBUG: Szczegóły wejść do self.ort_session.run() (get_text_embeddings_batch) ---")
        if model_inputs_filtered is None: logger.debug("DEBUG: model_inputs_filtered jest None!")
        else:
            logger.debug(f"DEBUG: Klucze w model_inputs_filtered: {list(model_inputs_filtered.keys())}")
        logger.debug(f"DEBUG: Nazwy wyjść dla sesji: {self.output_names}")
        logger.debug("--- KONIEC DEBUG ---")
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
# --- Koniec klasy CLIPImageEmbedder ---

app = FastAPI()
embedder: Optional[CLIPImageEmbedder] = None

@app.on_event("startup")
async def startup_event():
    global embedder
    logger.info("Uruchamianie serwera FastAPI, inicjalizacja CLIPImageEmbedder...")
    try:
        embedder = CLIPImageEmbedder() 
        if embedder and embedder.model_ort_instance and embedder.ort_session: 
             logger.info(f"CLIPImageEmbedder zainicjalizowany pomyślnie. Używane urządzenie: {embedder.effective_device}")
        else:
             logger.critical("Nie udało się w pełni zainicjalizować embeddera (model lub sesja ONNX is None).")
             embedder = None 
    except Exception as e:
        logger.critical(f"Krytyczny błąd podczas inicjalizacji CLIPImageEmbedder na starcie aplikacji: {e}", exc_info=True)
        embedder = None 

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
        logger.info(f"Przetwarzanie obrazu ze ścieżki: {data.path}")
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
        logger.info(f"Przetwarzanie załadowanego pliku: {file.filename}")
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
        logger.info(f"Przetwarzanie partii {len(data.paths)} obrazów.")
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
            details = f"Embedder częściowo zainicjalizowany (model, procesor lub sesja ONNX może brakować). Efektywne urządzenie: {current_device}."
            
    status = "ok" if initialized_fully else "error"
    return {"status": status, "embedder_fully_initialized": initialized_fully, "effective_device": current_device, "details": details}

if __name__ == "__main__":
    logger.info("Startowanie serwera Uvicorn dla CLIP embeddings...")
    uvicorn.run("clip_server:app", host="127.0.0.1", port=8000, log_level="debug", reload=True)