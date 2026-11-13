using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using DirectShowLib;

// One-frame webcam capture (DirectShow) for the intruder-photo feature.
// Runs on a background STA thread; every failure is silent (no camera,
// camera busy, permission off — the locker must never break over this).
public static class CameraCapture
{
    public static bool CaptureJpeg(string outPath)
    {
        IFilterGraph2 graph = null;
        ICaptureGraphBuilder2 builder = null;
        IBaseFilter source = null;
        ISampleGrabber grabber = null;
        IBaseFilter grabberFilter = null;
        IBaseFilter nullRenderer = null;
        IMediaControl control = null;
        try
        {
            var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            if (devices == null || devices.Length == 0) return false;

            graph = (IFilterGraph2)new FilterGraph();
            builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            DsError.ThrowExceptionForHR(builder.SetFiltergraph((IGraphBuilder)graph));

            DsError.ThrowExceptionForHR(
                graph.AddSourceFilterForMoniker(devices[0].Mon, null, "HamiiCam", out source));

            grabber = (ISampleGrabber)new SampleGrabber();
            grabberFilter = (IBaseFilter)grabber;
            var mt = new AMMediaType
            {
                majorType = MediaType.Video,
                subType = MediaSubType.RGB24,
                formatType = FormatType.VideoInfo,
            };
            DsError.ThrowExceptionForHR(grabber.SetMediaType(mt));
            DsUtils.FreeAMMediaType(mt);
            DsError.ThrowExceptionForHR(graph.AddFilter(grabberFilter, "HamiiGrabber"));

            nullRenderer = (IBaseFilter)new NullRenderer();
            DsError.ThrowExceptionForHR(graph.AddFilter(nullRenderer, "HamiiNull"));

            DsError.ThrowExceptionForHR(builder.RenderStream(
                PinCategory.Capture, MediaType.Video, source, grabberFilter, nullRenderer));

            DsError.ThrowExceptionForHR(grabber.SetBufferSamples(true));
            DsError.ThrowExceptionForHR(grabber.SetOneShot(false));

            control = (IMediaControl)graph;
            DsError.ThrowExceptionForHR(control.Run());

            // Wait for the camera to warm up and deliver a frame (~4 s max).
            int size = 0;
            for (int i = 0; i < 40; i++)
            {
                size = 0;
                int hr = grabber.GetCurrentBuffer(ref size, IntPtr.Zero);
                if (hr >= 0 && size > 0) break;
                size = 0;
                Thread.Sleep(100);
            }
            if (size <= 0) return false;

            byte[] buffer = new byte[size];
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                DsError.ThrowExceptionForHR(
                    grabber.GetCurrentBuffer(ref size, pin.AddrOfPinnedObject()));
            }
            finally { pin.Free(); }

            // Connected media type → frame dimensions.
            var cmt = new AMMediaType();
            DsError.ThrowExceptionForHR(grabber.GetConnectedMediaType(cmt));
            var vih = (VideoInfoHeader)Marshal.PtrToStructure(cmt.formatPtr, typeof(VideoInfoHeader));
            int w = vih.BmiHeader.Width;
            int h = Math.Abs(vih.BmiHeader.Height);
            DsUtils.FreeAMMediaType(cmt);
            if (w <= 0 || h <= 0) return false;

            int stride = w * 3;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    // Sample-grabber buffers are bottom-up: flip rows.
                    for (int row = 0; row < h; row++)
                    {
                        IntPtr dst = IntPtr.Add(data.Scan0, row * data.Stride);
                        int srcOff = (h - 1 - row) * stride;
                        Marshal.Copy(buffer, srcOff, dst, Math.Min(stride, data.Stride));
                    }
                }
                finally { bmp.UnlockBits(data); }

                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                var enc = GetJpegEncoder();
                if (enc != null)
                {
                    var pars = new EncoderParameters(1);
                    pars.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
                    bmp.Save(outPath, enc, pars);
                }
                else bmp.Save(outPath, ImageFormat.Jpeg);
            }
            return File.Exists(outPath) && new FileInfo(outPath).Length > 0;
        }
        catch (Exception ex)
        {
            Core.Log("Camera capture failed: " + ex.Message);
            return false;
        }
        finally
        {
            try { if (control != null) control.Stop(); } catch { }
            Release(builder); Release(source); Release(grabberFilter);
            Release(nullRenderer); Release(graph);
            if (grabber != null) Release((object)grabber);
        }
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
        return null;
    }

    private static void Release(object o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
        catch { }
    }
}
