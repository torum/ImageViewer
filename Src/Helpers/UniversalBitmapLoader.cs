using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia;

namespace ImageViewer.Helpers;

public static class UniversalBitmapLoader
{
    public static Bitmap? LoadAnyImage(string filePath, Vector displayDpi, int hardwareDisplayWidth, int hardwareDisplayHeight)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Image file not found.", filePath);

        string extension = Path.GetExtension(filePath);

        // Native Avalonia Route (JPEG, PNG, WebP, GIF, BMP)
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) 
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) 
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) 
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) 
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) 
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            //return new Bitmap(filePath);

            // We will use a more memory-efficient, performance-oriented approach for large images

            Bitmap finalBitmap;

            // Open the file stream once
            using (var stream = File.OpenRead(filePath))
            {
                // First, instantiate a standard Bitmap context. 
                // This reads only metadata info or maps the layout array without fully uncompressing.
                var nativeMetaInfo = new Bitmap(stream);
                var sourcePixelSize = nativeMetaInfo.PixelSize;

                // Check if the image width is physically larger than your active screen resolution
                //if (nativeMetaInfo.Size.Width > hardwareDisplayWidth || nativeMetaInfo.Size.Height > hardwareDisplayHeight)
                if (sourcePixelSize.Width > hardwareDisplayWidth || sourcePixelSize.Height > hardwareDisplayHeight)
                {
                    // LARGE IMAGE DETECTED -> Downscale to match display width smoothly
                    // We reset the stream index position to read it cleanly from the beginning
                    stream.Seek(0, SeekOrigin.Begin);

                    //finalBitmap = Bitmap.DecodeToWidth(stream, hardwareDisplayWidth);

                    //double widthRatio = (double)hardwareDisplayWidth / nativeMetaInfo.Size.Width;
                    //double heightRatio = (double)hardwareDisplayHeight / nativeMetaInfo.Size.Height;
                    double widthRatio = (double)hardwareDisplayWidth / sourcePixelSize.Width;
                    double heightRatio = (double)hardwareDisplayHeight / sourcePixelSize.Height;

                    // Use the smaller ratio to ensure the image fits entirely within the display boundary
                    if (widthRatio < heightRatio)
                    {
                        finalBitmap = Bitmap.DecodeToWidth(stream, hardwareDisplayWidth);
                    }
                    else
                    {
                        finalBitmap = Bitmap.DecodeToHeight(stream, hardwareDisplayHeight);
                    }

                    // Dispose of the initial placeholder data now that we have our optimized copy
                    nativeMetaInfo.Dispose();
                }
                else
                {
                    // SMALL IMAGE DETECTED -> Keep original size to preserve quality
                    // Since nativeMetaInfo already holds the image at 100% true layout scale, use it!
                    finalBitmap = nativeMetaInfo;
                }
            }

            return finalBitmap;
        }

        // SkiaSharp Route (AVIF)
        if (extension.Equals(".avif", StringComparison.OrdinalIgnoreCase))
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            return AvifImageDecoder.DecodeAvifToAvalonia(fileStream, displayDpi);

        }
        /*
        //  (JPEG XL / .jxl)
        if (extension.Equals(".jxl", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeJxlToAvalonia(filePath);
        }
        */

        throw new NotSupportedException($"The extension {extension} is not supported.");
    }
}