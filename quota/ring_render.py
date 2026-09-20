"""Supersampled Pillow rings shared by native quota widgets."""
import math


def _percent(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return None
    return max(0.0, min(100.0, float(value)))


def render(size, quota, time_remaining, stale=False, background='#ffffff', quota_color=None, time_color=None):
    """Return a smooth exact-size RGBA donut image.

    Unknown or stale data receives only the neutral outer track. A missing time
    value omits the inner ring completely, while zero and full values remain
    distinct provider-reported states.
    """
    from PIL import Image, ImageDraw
    if not isinstance(size, int) or size < 16:
        raise ValueError('size must be an integer of at least 16')
    scale = 4
    image = Image.new('RGBA', (size * scale, size * scale), background)
    draw = ImageDraw.Draw(image)
    quota = _percent(quota)
    time_remaining = _percent(time_remaining)
    outer_width, inner_width = 6 * scale, 2 * scale
    margin = 4 * scale
    outer = (margin, margin, size * scale - margin, size * scale - margin)
    track = '#d7dee7'
    draw.ellipse(outer, outline=track, width=outer_width)
    if quota is not None and not stale:
        color = quota_color or ('#22a06b' if quota >= 30 else '#d18a17' if quota >= 10 else '#c2413b')
        if quota == 100:
            draw.ellipse(outer, outline=color, width=outer_width)
        elif quota > 0:
            draw.arc(outer, start=90, end=90 - quota * 3.6, fill=color, width=outer_width)
    if time_remaining is not None and not stale:
        inset = 10 * scale
        inner = (inset, inset, size * scale - inset, size * scale - inset)
        draw.ellipse(inner, outline='#d7dee7', width=inner_width)
        color = time_color or '#4777ad'
        if time_remaining == 100:
            draw.ellipse(inner, outline=color, width=inner_width)
        elif time_remaining > 0:
            draw.arc(inner, start=90, end=90 - time_remaining * 3.6, fill=color, width=inner_width)
    return image.resize((size, size), Image.Resampling.LANCZOS)


render_rings = render
