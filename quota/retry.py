"""Validate provider waits without shortening a valid cooldown."""
import math

# The native interface displays dates through DateTimeOffset, up to year 9999.
MAX_TIMESTAMP = 253402300799


def delay_seconds(value):
    """Invalid hints use ordinary backoff; large valid integers stay valid."""
    if type(value) is int:
        return max(0, value)
    if type(value) is float and math.isfinite(value):
        return max(0, value)
    return 0


def deadline(now, delay):
    """None means the valid wait cannot be represented and must stay paused."""
    if delay > MAX_TIMESTAMP - now:
        return None
    result = now + delay
    return result if math.isfinite(result) and 0 <= result <= MAX_TIMESTAMP else None


def finite_number(value):
    # math.isfinite(huge_int) itself overflows, so integers are handled first.
    return type(value) is int or type(value) is float and math.isfinite(value)
