#if CHAMELAB_UI_TESTS
using System;
using System.IO;
using Android.Content;
using Android.Graphics;
using Android.Util;

namespace ChameLab.Android;

internal static class SplashScreenCheck
{
    // Runs against Android's inflated drawable at each emulator's actual density.
    // A screenshot alone can hide clipped pixels behind the system splash mask.
    public static void Run(Context context, string outputDirectory)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return;
        using var value = new TypedValue();
        if (!context.Theme!.ResolveAttribute(global::Android.Resource.Attribute.WindowSplashScreenAnimatedIcon, value, true)
            || value.ResourceId != Resource.Drawable.splash_icon_padded)
            throw new InvalidOperationException("The launch theme must use the padded splash icon.");
        using var drawable = context.GetDrawable(value.ResourceId)
            ?? throw new InvalidOperationException("Splash drawable is missing.");
        var size = drawable.IntrinsicWidth;
        if (size <= 0 || drawable.IntrinsicHeight != size)
            throw new InvalidOperationException("Splash drawable must have a square intrinsic size.");
        using var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        drawable.SetBounds(0, 0, size, size);
        drawable.Draw(canvas);
        var pixels = new int[size * size];
        bitmap.GetPixels(pixels, 0, size, 0, 0, size, size);
        var visible = 0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if ((pixels[y * size + x] >>> 24) == 0) continue;
                visible++;
                var dx = x + .5 - size / 2.0;
                var dy = y + .5 - size / 2.0;
                if (dx * dx + dy * dy > size * size / 9.0)
                    throw new InvalidOperationException("Android's splash mask would clip the artwork.");
            }
        if (visible < 100) throw new InvalidOperationException("Splash artwork is empty.");
        Directory.CreateDirectory(outputDirectory);
        using var output = File.Create(System.IO.Path.Combine(outputDirectory, "android-splash-icon.png"));
        bitmap.Compress(Bitmap.CompressFormat.Png!, 100, output);
    }
}
#endif
