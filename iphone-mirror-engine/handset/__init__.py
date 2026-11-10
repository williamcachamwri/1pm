import importlib.util
import sys
from pathlib import Path

if importlib.util.find_spec("lzss") is None or importlib.util.find_spec("lzfse") is None:
    sys.path.append(str(Path(__file__).resolve().parent / "_shims"))
