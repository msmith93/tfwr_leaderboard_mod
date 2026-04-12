#!/home/msmith/venvs/venv/bin/python3
import argparse
import subprocess
import sys
import numpy as np

try:
    from kokoro import KPipeline
except ImportError:
    print("Kokoro not installed. Run: pip install kokoro soundfile")
    sys.exit(1)

try:
    import soundfile as sf
except ImportError:
    print("soundfile not installed. Run: pip install soundfile")
    sys.exit(1)

SAMPLE_RATE = 24000
DEFAULT_VOICE = "am_adam"
DEFAULT_OUTPUT = "tts_output.wav"

# Available voices:
#   American English female:  af_alloy, af_aoede, af_bella, af_heart, af_jessica,
#                             af_kore, af_nicole, af_nova, af_river, af_sarah, af_sky
#   American English male:    am_adam, am_echo, am_eric, am_fenrir, am_liam,
#                             am_michael, am_onyx, am_puck, am_santa
#   British English female:   bf_alice, bf_emma, bf_isabella, bf_lily
#   British English male:     bm_daniel, bm_fable, bm_george, bm_lewis
#   Spanish female:           ef_dora
#   Spanish male:             em_alex, em_santa
#   French female:            ff_siwis
#   Hindi female:             hf_alpha, hf_beta
#   Hindi male:               hm_omega, hm_psi
#   Italian female:           if_sara
#   Italian male:             im_nicola
#   Japanese female:          jf_alpha, jf_gongitsune, jf_nezumi, jf_tebukuro
#   Japanese male:            jm_kumo
#   Portuguese female:        pf_dora
#   Portuguese male:          pm_alex, pm_santa
#   Chinese female:           zf_xiaobei, zf_xiaoni, zf_xiaoxiao, zf_xiaoyi
#   Chinese male:             zm_yunjian, zm_yunxi, zm_yunxia, zm_yunyang


def synthesize(text, voice, output_path, no_play):
    pipeline = KPipeline(lang_code="a")

    device = "cuda" if _cuda_available() else "cpu"
    if device == "cuda":
        gpu_name = _gpu_name()
        print(f"Using device: cuda ({gpu_name})")
    else:
        print("Using device: cpu (no CUDA available)")

    print(f"Synthesizing with voice '{voice}'...")

    chunks = []
    for _, _, audio in pipeline(text, voice=voice):
        chunks.append(audio)

    audio_data = np.concatenate(chunks)
    sf.write(output_path, audio_data, SAMPLE_RATE)
    print(f"Saved: {output_path}")

    if not no_play:
        _play(output_path)


def _play(path):
    players = [
        ["aplay", path],
        ["vlc", "--play-and-exit", "--intf", "dummy", path],
    ]
    for cmd in players:
        if _which(cmd[0]):
            subprocess.run(cmd)
            return
    print("No audio player found (tried aplay, vlc). Install one to enable playback.")


def _which(name):
    return subprocess.run(["which", name], capture_output=True).returncode == 0


def _cuda_available():
    try:
        import torch
        return torch.cuda.is_available()
    except ImportError:
        return False


def _gpu_name():
    try:
        import torch
        return torch.cuda.get_device_name(0)
    except Exception:
        return "unknown"


def main():
    parser = argparse.ArgumentParser(description="GPU-accelerated text-to-speech via Kokoro")
    parser.add_argument("text", help="Text to synthesize")
    parser.add_argument("--output", default=DEFAULT_OUTPUT, help=f"Output WAV path (default: {DEFAULT_OUTPUT})")
    parser.add_argument("--voice", default=DEFAULT_VOICE, help=f"Voice name (default: {DEFAULT_VOICE})")
    parser.add_argument("--no-play", action="store_true", help="Save WAV without playing it")
    args = parser.parse_args()

    synthesize(args.text, args.voice, args.output, args.no_play)


if __name__ == "__main__":
    main()
