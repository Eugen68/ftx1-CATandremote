using Ftx1.Mobile;
using Ftx1.Client;
using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.Graphics;
using global::Android.Hardware.Camera2;
using global::Android.Hardware.Camera2.Params;
using global::Android.Media;
using global::Android.OS;
using global::Android.Views;
using global::Android.Widget;
using ZXing;
using ZXing.Common;
namespace Ftx1.Android;
[Activity(Exported = false, ScreenOrientation = ScreenOrientation.Portrait)]
public class QrActivity : Activity, TextureView.ISurfaceTextureListener
{
    TextureView? preview; TextView? status; Button? confirm; string? readyText;
    CameraDevice? camera; CameraCaptureSession? session; ImageReader? reader; HandlerThread? thread; Handler? handler; Surface? surface;
    volatile bool closed, completed; int generation, sensor, width = 640, height = 480; bool continuousFocus;
    static BarcodeReaderGeneric NewDecoder() => new() { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } } };
    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state); Title = MobileApp.Text("MobileScanQr");
        var layout = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        status = new TextView(this) { Text = MobileApp.Text("MobileQrHint"), TextSize = 18 }; layout.AddView(status);
        preview = new TextureView(this) { SurfaceTextureListener = this }; layout.AddView(preview, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        confirm = new Button(this) { Text = MobileApp.Text("MobileQrUse"), Enabled = false };
        confirm.Click += (_, _) =>
        {
            if (closed || completed || !PairingScan.TryParse(readyText, out _)) return;
            completed = true; SetResult(global::Android.App.Result.Ok, new Intent().PutExtra("pairing", readyText)); Finish();
        };
        layout.AddView(confirm); SetContentView(layout);
    }
    protected override void OnResume() { base.OnResume(); closed = false; if (preview?.IsAvailable == true) Open(preview.SurfaceTexture!); }
    public void OnSurfaceTextureAvailable(SurfaceTexture texture, int viewWidth, int viewHeight) => Open(texture);
    void Open(SurfaceTexture texture)
    {
        if (closed || completed || thread != null) return;
        if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted) { ReportCameraError("MobileCameraDenied"); return; }
        try
        {
            readyText = null; confirm!.Enabled = false; status!.Text = MobileApp.Text("MobileQrHint");
            using var manager = (CameraManager)GetSystemService(CameraService)!;
            string id = manager.GetCameraIdList().FirstOrDefault(i => (manager.GetCameraCharacteristics(i).Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue() == (int)LensFacing.Back) ?? manager.GetCameraIdList().First();
            using var characteristics = manager.GetCameraCharacteristics(id);
            sensor = (characteristics.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer)?.IntValue() ?? 0;
            continuousFocus = characteristics.Get(CameraCharacteristics.ControlAfAvailableModes) is global::Android.Runtime.JavaArray<int> modes && modes.Contains((int)ControlAFMode.ContinuousPicture);
            using var map = (StreamConfigurationMap?)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
            var sizes = map?.GetOutputSizes((int)ImageFormatType.Yuv420888) ?? throw new InvalidOperationException("CameraUnavailable");
            var previews = map!.GetOutputSizes(Java.Lang.Class.FromType(typeof(SurfaceTexture))) ?? [];
            var common = sizes.Where(s => previews.Any(p => p.Width == s.Width && p.Height == s.Height)).ToArray();
            var candidates = common.Where(s => s.Width <= 1280 && s.Height <= 960).OrderByDescending(s => s.Width * s.Height).ToArray();
            var chosen = candidates.FirstOrDefault() ?? common.OrderBy(s => s.Width * s.Height).First(); width = chosen.Width; height = chosen.Height;
            texture.SetDefaultBufferSize(width, height); surface = new Surface(texture); Transform();
            thread = new HandlerThread("FTX1.QR"); thread.Start(); handler = new Handler(thread.Looper!);
            int current = ++generation;
            reader = ImageReader.NewInstance(width, height, ImageFormatType.Yuv420888, 2)!; reader.SetOnImageAvailableListener(new Frames(this, current), handler);
            manager.OpenCamera(id, new Opened(this, current), handler);
        }
        catch (Exception) { ReportCameraError("MobileCameraUnavailable"); }
    }
    void Transform()
    {
        if (preview == null || preview.Width == 0 || preview.Height == 0) return;
        int rotation = (int)(preview.Display?.Rotation ?? SurfaceOrientation.Rotation0) * 90;
        using var matrix = new Matrix();
        matrix.SetPolyToPoly(new float[] { 0, 0, preview.Width, 0, 0, preview.Height }, 0,
            CameraFrame.PreviewCorners(width, height, sensor, rotation, preview.Width, preview.Height), 0, 3);
        preview.SetTransform(matrix);
    }
    void StartSession(CameraDevice device, int current)
    {
        if (closed || current != generation) { device.Close(); return; } camera = device;
        try
        {
#pragma warning disable CS0618
            device.CreateCaptureSession(new List<Surface> { surface!, reader!.Surface! }, new Configured(this, current), handler);
#pragma warning restore CS0618
        }
        catch (Exception) { ReportCameraError("MobileCameraUnavailable"); }
    }
    void Decode(ImageReader source, Frames frame)
    {
        try
        {
            using var image = source.AcquireLatestImage(); if (image == null || completed || closed || frame.Generation != generation) return;
            long now = System.Environment.TickCount64; if (now - frame.LastDecode < 150) return; frame.LastDecode = now;
            var plane = image.GetPlanes()![0]; var input = plane.Buffer!; var bytes = new byte[input.Remaining()]; input.Get(bytes);
            var gray = CameraFrame.Gray(bytes, image.Width, image.Height, plane.RowStride, plane.PixelStride);
            var result = frame.Decoder.Decode(gray, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8);
            frame.Scan.Observe(result?.Text, now); var text = frame.Scan.Text; var address = frame.Scan.Address;
            RunOnUiThread(() =>
            {
                if (closed || completed || frame.Generation != generation) return; readyText = text; confirm!.Enabled = text != null;
                status!.Text = text == null ? MobileApp.Text("MobileQrHint") : string.Format(System.Globalization.CultureInfo.InvariantCulture, MobileApp.Text("MobileQrRecognized"), address);
            });
        }
        catch (Exception) { /* A closing ImageReader or blurred/non-pairing frame never ends the scan. */ }
    }
    void ReportCameraError(string key) => RunOnUiThread(() => { if (closed || completed) return; MobileRuntime.Model.Notice = MobileApp.Text(key); Finish(); });
    void CloseCamera()
    {
        closed = true; generation++;
        void Close(Action action) { try { action(); } catch (Exception) { } }
        Close(() => session?.Close()); session = null; Close(() => camera?.Close()); camera = null;
        Close(() => reader?.Close()); reader = null; Close(() => surface?.Dispose()); surface = null;
        Close(() => thread?.QuitSafely()); thread = null; handler = null;
    }
    protected override void OnPause() { CloseCamera(); base.OnPause(); }
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surfaceTexture) { CloseCamera(); return true; }
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surfaceTexture, int viewWidth, int viewHeight) => Transform();
    public void OnSurfaceTextureUpdated(SurfaceTexture surfaceTexture) { }
    class Frames(QrActivity owner, int current) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public int Generation { get; } = current; public long LastDecode;
        public PairingScan Scan { get; } = new(); public BarcodeReaderGeneric Decoder { get; } = NewDecoder();
        public void OnImageAvailable(ImageReader? source) { if (source != null) owner.Decode(source, this); }
    }
    class Opened(QrActivity owner, int current) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => owner.StartSession(camera, current);
        public override void OnDisconnected(CameraDevice camera) { camera.Close(); if (current == owner.generation) owner.ReportCameraError("MobileCameraUnavailable"); }
        public override void OnError(CameraDevice camera, CameraError error) { camera.Close(); if (current == owner.generation) owner.ReportCameraError("MobileCameraUnavailable"); }
    }
    class Configured(QrActivity owner, int current) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            if (owner.closed || current != owner.generation) { session.Close(); return; } owner.session = session;
            try
            {
                using var request = owner.camera!.CreateCaptureRequest(CameraTemplate.Preview)!; request.AddTarget(owner.surface!); request.AddTarget(owner.reader!.Surface!);
                if (owner.continuousFocus) request.Set(CaptureRequest.ControlAfMode!, new Java.Lang.Integer((int)ControlAFMode.ContinuousPicture));
                session.SetRepeatingRequest(request.Build()!, null, owner.handler);
            }
            catch (Exception) { owner.ReportCameraError("MobileCameraUnavailable"); }
        }
        public override void OnConfigureFailed(CameraCaptureSession session) { if (current == owner.generation) owner.ReportCameraError("MobileCameraUnavailable"); }
    }
}
