using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Platform;
using System.Diagnostics;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SkiaSharp;

namespace ImageViewer.Helpers;

public static class UniversalBitmapLoader
{
    public static Bitmap? LoadAnyImage(string filePath, Vector displayDpi, int hardwareDisplayWidth, int hardwareDisplayHeight)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Image file not found.", filePath);

        int orientation = GetExifOrientation(filePath);
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
                    //Debug.WriteLine($"Large image detected: {sourcePixelSize.Width}x{sourcePixelSize.Height}. Downscaling to fit display: {hardwareDisplayWidth}x{hardwareDisplayHeight}.");

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
                        //Debug.WriteLine($"Width ratio ({widthRatio}) is smaller than height ratio ({heightRatio}). Decoding to width.");
                        finalBitmap = Bitmap.DecodeToWidth(stream, hardwareDisplayWidth);
                    }
                    else
                    {
                        //Debug.WriteLine($"Height ratio ({heightRatio}) is smaller than width ratio ({widthRatio}). Decoding to height.");
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

            return ApplyExifOrientation(finalBitmap, orientation);
        }

        // SkiaSharp Route (AVIF)
        if (extension.Equals(".avif", StringComparison.OrdinalIgnoreCase))
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var avifBitmap = AvifImageDecoder.DecodeAvifToAvalonia(fileStream, displayDpi);
            return avifBitmap is null ? null : ApplyExifOrientation(avifBitmap, orientation);

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

    private static int GetExifOrientation(string filePath)
    {
        var metadata = ImageMetadataReader.ReadMetadata(filePath);
        var exifDirectory = metadata.OfType<ExifIfd0Directory>().FirstOrDefault();

        return exifDirectory is not null
            && exifDirectory.TryGetInt32(ExifDirectoryBase.TagOrientation, out int orientation)
            && orientation is >= 1 and <= 8
                ? orientation
                : 1;
    }

    private static Bitmap ApplyExifOrientation(Bitmap bitmap, int orientation)
    {
        if (orientation == 1)
        {
            return bitmap;
        }

        try
        {
            using var encodedBitmap = new MemoryStream();
            bitmap.Save(encodedBitmap, PngBitmapEncoderOptions.Default);
            encodedBitmap.Position = 0;

            using var sourceBitmap = SKBitmap.Decode(encodedBitmap);
            if (sourceBitmap is null || sourceBitmap.IsEmpty)
            {
                return bitmap;
            }

            bool swapsDimensions = orientation >= 5;
            int width = swapsDimensions ? sourceBitmap.Height : sourceBitmap.Width;
            int height = swapsDimensions ? sourceBitmap.Width : sourceBitmap.Height;
            using var orientedBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var orientationMatrix = orientation switch
            {
                2 => new SKMatrix { ScaleX = -1, TransX = sourceBitmap.Width, ScaleY = 1, Persp2 = 1 },
                3 => new SKMatrix { ScaleX = -1, TransX = sourceBitmap.Width, ScaleY = -1, TransY = sourceBitmap.Height, Persp2 = 1 },
                4 => new SKMatrix { ScaleX = 1, ScaleY = -1, TransY = sourceBitmap.Height, Persp2 = 1 },
                5 => new SKMatrix { SkewX = 1, SkewY = 1, Persp2 = 1 },
                6 => new SKMatrix { SkewX = -1, TransX = sourceBitmap.Height, SkewY = 1, Persp2 = 1 },
                7 => new SKMatrix { SkewX = -1, TransX = sourceBitmap.Height, SkewY = -1, TransY = sourceBitmap.Width, Persp2 = 1 },
                8 => new SKMatrix { SkewX = 1, SkewY = -1, TransY = sourceBitmap.Width, Persp2 = 1 },
                _ => SKMatrix.Identity
            };

            using (var canvas = new SKCanvas(orientedBitmap))
            {
                canvas.SetMatrix(orientationMatrix);
                canvas.DrawBitmap(sourceBitmap, new SKPoint(0, 0), SKSamplingOptions.Default);
            }

            var result = new WriteableBitmap(
                new PixelSize(width, height),
                bitmap.Dpi,
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var lockedBuffer = result.Lock())
            {
                unsafe
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            (byte*)orientedBitmap.GetPixels() + y * orientedBitmap.RowBytes,
                            (byte*)lockedBuffer.Address + y * lockedBuffer.RowBytes,
                            lockedBuffer.RowBytes,
                            width * 4);
                    }
                }
            }

            bitmap.Dispose();
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to apply EXIF orientation {orientation}: {ex}");
            return bitmap;
        }
    }
}