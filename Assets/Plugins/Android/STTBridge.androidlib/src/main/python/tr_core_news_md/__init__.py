from pathlib import Path
from spacy.util import load_model_from_init_py, get_model_meta, load_model_from_path



__version__ = get_model_meta(Path(__file__).parent)['version']


def load(**overrides):
  return load_model_from_path(Path(__file__).parent, **overrides)

