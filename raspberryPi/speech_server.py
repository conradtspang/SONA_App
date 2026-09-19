import os
from flask import Flask, request, jsonify, send_file
import speech_recognition as sr
import tempfile
import pyttsx3
import traceback

app = Flask(__name__)
recognizer = sr.Recognizer()

@app.route('/transcribe', methods=['POST'])
def transcribe():
    """
    Speech-To-Text (STT) using SpeechRecognition library
    """
    if 'file' not in request.files:
        print("Error: No file uploaded")
        return jsonify({"error": "No file uploaded"}), 400
    
    file = request.files['file']
    language = request.form.get('language', 'en-US')
    
    # Save to temp file
    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as temp_wav:
        temp_path = temp_wav.name
        file.save(temp_path)

    try:
        print(f"Received audio file: {temp_path}, Language: {language}")

        # Load audio file
        with sr.AudioFile(temp_path) as source:
            print("Reading audio data...")
            audio_data = recognizer.record(source)
            
            print(f"Sending to Google Speech API with language {language}...")
            text = recognizer.recognize_google(audio_data, language=language)
            print(f"Result: {text}")
            
            return jsonify({"text": text})

    except sr.UnknownValueError:
        print("Error: Speech not understood (UnknownValueError)")
        return jsonify({"error": "Speech not understood"}), 400
    except sr.RequestError as e:
        print(f"Error: Google API Request failed: {e}")
        return jsonify({"error": f"Service error: {e}"}), 500
    except Exception as e:
        print("!!! CRITICAL ERROR !!!")
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500
    finally:
        if os.path.exists(temp_path):
            os.remove(temp_path)

@app.route('/tts', methods=['POST'])
def tts():
    """
    Text-To-Speech (TTS) using gTTS (for native accent) or pyttsx3 (fallback)
    """
    try:
        data = request.get_json()
        text = data['text']
        language = data.get('language', 'en-US')
        gender = data.get('gender', 'female').lower()
        print(f"TTS Request: {text} (Language: {language}, Gender: {gender})")
        
        # Mapping language codes for gTTS
        gtts_lang = 'en'
        if language.startswith('es'): gtts_lang = 'es'
        elif language.startswith('zh'): gtts_lang = 'zh-CN'
        elif language.startswith('en'): gtts_lang = 'en'

        # Process text (e.g., 1.6 -> 1 point 6)
        import re
        if gtts_lang == 'es':
            text = re.sub(r'(\d+)\.(\d+)', r'\1 punto \2', text)
        elif gtts_lang == 'zh-CN':
            text = re.sub(r'(\d+)\.(\d+)', r'\1点\2', text)
        else:
            text = re.sub(r'(\d+)\.(\d+)', r'\1 point \2', text)

        # Try gTTS first for high-quality native accent
        # Note: gTTS doesn't support gender directly, it's usually just one voice per language.
        try:
            from gtts import gTTS
            import subprocess
            print(f"Using gTTS for language: {gtts_lang}")
            tts_obj = gTTS(text=text, lang=gtts_lang)
            
            temp_mp3 = tempfile.NamedTemporaryFile(suffix=".mp3", delete=False)
            temp_mp3_path = temp_mp3.name
            temp_mp3.close()
            
            tts_obj.save(temp_mp3_path)
            
            # Convert MP3 to WAV using ffmpeg for Unity compatibility
            temp_wav_path = temp_mp3_path.replace(".mp3", ".wav")
            try:
                subprocess.run(['ffmpeg', '-y', '-i', temp_mp3_path, temp_wav_path], 
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
                os.remove(temp_mp3_path) # Clean up MP3
                return send_file(temp_wav_path, mimetype="audio/wav")
            except Exception as ffmpeg_err:
                print(f"FFmpeg conversion failed: {ffmpeg_err}")
                return send_file(temp_mp3_path, mimetype="audio/mpeg") # Fallback to MP3

        except Exception as gtts_err:
            print(f"gTTS failed or not available: {gtts_err}. Falling back to pyttsx3.")
            
            engine = pyttsx3.init()
            engine.setProperty('rate', 150)
            voices = engine.getProperty('voices')
            voice_found = False
            
            # Priority search for voice matching language AND gender
            search_key = 'spanish' if gtts_lang == 'es' else 'chinese' if gtts_lang == 'zh-CN' else 'english'
            
            # First pass: look for both language and gender
            for voice in voices:
                v_name = voice.name.lower()
                v_id = voice.id.lower()
                if search_key in v_name or gtts_lang in v_id:
                    if gender in v_name or gender in v_id:
                        engine.setProperty('voice', voice.id)
                        print(f"pyttsx3: Switched to {gender} {search_key} voice: {voice.name}")
                        voice_found = True
                        break
            
            # Second pass: just language if gender match failed
            if not voice_found:
                for voice in voices:
                    if search_key in voice.name.lower() or gtts_lang in voice.id.lower():
                        engine.setProperty('voice', voice.id)
                        print(f"pyttsx3: Match failed for {gender}, using system {search_key} voice: {voice.name}")
                        voice_found = True
                        break
            
            if not voice_found:
                print(f"pyttsx3: No specific voice found for {language}. Using system default.")

            temp_wav = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
            temp_path = temp_wav.name
            temp_wav.close()

            engine.save_to_file(text, temp_path)
            engine.runAndWait()
            return send_file(temp_path, mimetype="audio/wav")

    except Exception as e:
        print(f"TTS Critical Error: {e}")
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500

if __name__ == '__main__':
    print("Starting Speech Server on port 5007...")
    app.run(host='0.0.0.0', port=5007, debug=True)
