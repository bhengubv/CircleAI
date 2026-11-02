// AndroidImageShrink.cs
//
// THE PICTURE MADE AS SMALL AS A VISION MODEL CAN USE, ON ANDROID'S OWN DECODER (Step 4).
//
// ImageBudget decides from the header alone whether a picture is bigger than a vision model will
// resolve, and by what power of two to subsample it; it deliberately decodes nothing. This is the
// half it leaves to the platform: BitmapFactory subsamples WHILE decoding, so a twelve-megapixel
// photo never exists in memory at full size, and the result is written back as a JPEG. Used by an
// app before a picture crosses the link, and by the service again for one that arrived unshrunk.

using Android.Graphics;

namespace CircleAI.Device;

/// <summary>Shrinks an encoded picture to an edge budget with Android's decoder.</summary>
public static class AndroidImageShrink
{
    /// <summary>JPEG quality for a shrunk picture: plenty for a vision model, a fraction of the bytes.</summary>
    public const int Quality = 90;

    /// <summary>
    /// The picture with its long edge at most <paramref name="maxEdge"/>, re-encoded as JPEG; the
    /// picture as it came when it is already within budget, cannot be measured, or will not decode.
    /// </summary>
    public static byte[] Shrink(byte[] image, int maxEdge = CircleAI.Inference.ImageBudget.MaxEdge)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!CircleAI.Inference.ImageBudget.NeedsShrinking(image, maxEdge)) return image;

        try
        {
            var sample = CircleAI.Inference.ImageBudget.SampleFor(image, maxEdge);
            using var options = new BitmapFactory.Options { InSampleSize = Math.Max(1, sample) };
            using var decoded = BitmapFactory.DecodeByteArray(image, 0, image.Length, options);
            if (decoded is null) return image;

            // THE LAST STEP EXACTLY: a power of two undershoots by design, so a picture between one
            // and two budgets wide is scaled to the budget rather than halved below it.
            var bitmap = decoded;
            var edge = Math.Max(decoded.Width, decoded.Height);
            if (edge > maxEdge)
            {
                var scale = (double)maxEdge / edge;
                bitmap = Bitmap.CreateScaledBitmap(decoded,
                    Math.Max(1, (int)Math.Round(decoded.Width * scale)),
                    Math.Max(1, (int)Math.Round(decoded.Height * scale)), filter: true) ?? decoded;
            }

            try
            {
                using var output = new System.IO.MemoryStream();
                if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, Quality, output)) return image;
                var shrunk = output.ToArray();
                return shrunk.Length > 0 ? shrunk : image;
            }
            finally
            {
                if (!ReferenceEquals(bitmap, decoded)) bitmap.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A picture this decoder will not read is sent as it came; the vision side has its own limit.
            return image;
        }
    }
}
