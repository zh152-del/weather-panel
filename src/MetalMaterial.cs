using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WeatherPanel;

/// <summary>微表面噪声：64×64 稀疏灰点平铺，不透明度 3%，
/// 只消除"过于完美的塑料感"，不产生可见颗粒。生成一次后冻结复用。</summary>
internal static class MetalMaterial
{
    private static ImageBrush? _noise;

    public static ImageBrush NoiseBrush
    {
        get
        {
            if (_noise != null) return _noise;

            const int size = 64, stride = size * 4;
            var pixels = new byte[stride * size];
            var rnd = new Random(20261026);
            for (int i = 0; i < size * size; i++)
            {
                byte v = (byte)rnd.Next(150, 256);
                byte a = rnd.Next(0, 100) < 30 ? (byte)90 : (byte)0;
                int o = i * 4;
                pixels[o] = v; pixels[o + 1] = v; pixels[o + 2] = v; pixels[o + 3] = a;
            }

            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, stride, 0);
            bmp.Freeze();

            _noise = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, size, size),
                Stretch = Stretch.None,
            };
            _noise.Freeze();
            return _noise;
        }
    }
}
