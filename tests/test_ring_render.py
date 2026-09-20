import importlib.util
import unittest

from quota.ring_render import render


@unittest.skipUnless(importlib.util.find_spec('PIL'), 'Pillow optional desktop dependency')
class RingRenderTests(unittest.TestCase):
    def test_exact_size_and_antialiased_transparent_edges(self):
        image = render(60, 50, None, background=(255, 255, 255, 0))
        self.assertEqual(image.size, (60, 60))
        alpha = [pixel[3] for pixel in image.getdata()]
        self.assertIn(0, alpha)
        self.assertTrue(any(0 < value < 255 for value in alpha))

    def test_zero_full_unknown_and_stale_do_not_invent_partial_usage(self):
        zero = render(60, 0, None)
        full = render(60, 100, None)
        unknown = render(60, None, None)
        stale = render(60, 80, 50, stale=True)
        # At the upper outer ring full has colored quota while zero, unknown,
        # and stale retain only the neutral track.
        self.assertNotEqual(full.getpixel((30, 4)), zero.getpixel((30, 4)))
        self.assertEqual(zero.getpixel((30, 4)), unknown.getpixel((30, 4)))
        self.assertEqual(unknown.getpixel((30, 4)), stale.getpixel((30, 4)))

    def test_missing_time_ring_stays_absent(self):
        no_time = render(60, 50, None)
        with_time = render(60, 50, 100)
        self.assertNotEqual(no_time.getpixel((30, 10)), with_time.getpixel((30, 10)))


if __name__ == '__main__':
    unittest.main()
