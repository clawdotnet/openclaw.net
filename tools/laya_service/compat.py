"""Adapter improvements for Laya 0.3.4; no upstream files are patched at runtime."""
import unicodedata
from .protocol import Rejected


def select_checkpoint(state, language=None):
    from laya.common import serialize_state
    from laya.lang import analyse
    text = serialize_state(state)
    # Laya 0.3.4 omits Armenian from its script ranges. Inspect every letter,
    # including minority scripts, so mixed text cannot silently use English.
    non_latin = any(c.isalpha() and "LATIN" not in unicodedata.name(c, "") for c in text)
    if non_latin:
        if language and language.lower().split("-")[0] in ("en", "eng", "english"):
            raise Rejected("language_script_conflict")
        return "multilingual"
    if language:
        return "english" if language.lower().split("-")[0] in ("en", "eng", "english") else "multilingual"
    detection = analyse(state)
    return "english" if detection["is_english"] else "multilingual"


def ensure_complete(agent, state, questions):
    """Check the SDK's exact token construction before any silent truncation.

    Candidate descriptions have a separate per-option cap, and the question
    head has its own cap; checking only the overall state length is insufficient.
    """
    from laya.common import render_options, serialize_state
    tok = agent.tok
    mask = tok.mask_token
    def encode(text):
        if mask in text:
            # The SDK would replace marker strings; preserve exact input or reject.
            raise Rejected("reserved_token_in_input")
        return tok(text, add_special_tokens=False)["input_ids"]
    state_count = len(encode(serialize_state(state)))
    for q in questions.values():
        internal = agent._to_internal(q)
        options = render_options(internal)
        lengths = [1 + len(encode(" " + option)) for option in options]
        if any(length > 49 for length in lengths):
            raise Rejected("option_too_long")
        head_count = len(encode("%s question: %s" % (internal["t"], internal["ins"])))
        available = agent.cfg.get("head_max_len", 192) - sum(lengths)
        if available < 16 or head_count > max(8, available):
            raise Rejected("question_too_long")
        if 4 + head_count + sum(lengths) + state_count > agent.cfg.get("max_len", 512):
            raise Rejected("state_too_long")
