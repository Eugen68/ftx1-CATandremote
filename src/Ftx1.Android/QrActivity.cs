using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.Graphics;
using global::Android.Hardware.Camera2;
using global::Android.Media;
using global::Android.OS;
using global::Android.Views;
using global::Android.Widget;
using ZXing;
using ZXing.Common;
namespace Ftx1.Android;
[Activity(Exported = false, ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class QrActivity : Activity, TextureView.ISurfaceTextureListener
{
    TextureView? preview; CameraDevice? camera; CameraCaptureSession? session; ImageReader? reader; HandlerThread? thread; Handler? handler; Surface? surface; bool closed, completed;
    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state); Title = MobileApp.Text("MobileScanQr");
        var layout = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        layout.AddView(new TextView(this) { Text = MobileApp.Text("MobileQrHint"), TextSize = 18 }, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        preview = new TextureView(this) { SurfaceTextureListener = this }; layout.AddView(preview, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1)); SetContentView(layout);
    }
    public void OnSurfaceTextureAvailable(SurfaceTexture texture, int width, int height)
    {
        if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted) { Finish(); return; }
        try
        {
            thread = new HandlerThread("FTX1.QR"); thread.Start(); handler = new Handler(thread.Looper!);
            texture.SetDefaultBufferSize(640, 480); surface = new Surface(texture);
            reader = ImageReader.NewInstance(640, 480, ImageFormatType.Yuv420888, 2)!; reader.SetOnImageAvailableListener(new Frames(this), handler);
            using var manager = (CameraManager)GetSystemService(CameraService)!;
            string id = manager.GetCameraIdList().FirstOrDefault(i => (manager.GetCameraCharacteristics(i).Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue() == (int)LensFacing.Back) ?? manager.GetCameraIdList().First();
            manager.OpenCamera(id, new Opened(this), handler);
        }
        catch (Exception) { RunOnUiThread(() => { MobileRuntime.Model.Notice = MobileApp.Text("MobileCameraDenied"); Finish(); }); }
    }
    void StartSession(CameraDevice device)
    {
        if (closed) { device.Close(); return; } camera = device;
#pragma warning disable CS0618
        device.CreateCaptureSession(new List<Surface> { surface!, reader!.Surface! }, new Configured(this), handler);
#pragma warning restore CS0618
    }
    void Decode(ImageReader source)
    {
        using var image = source.AcquireLatestImage(); if (image == null || completed || closed) return;
        try
        {
            var plane = image.GetPlanes()![0]; var input = plane.Buffer!; var bytes = new byte[input.Remaining()]; input.Get(bytes); var gray = new byte[image.Width * image.Height];
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) { int index = y * plane.RowStride + x * plane.PixelStride; if (index < bytes.Length) gray[y * image.Width + x] = bytes[index]; }
            var decoder = new BarcodeReaderGeneric { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } } };
            var result = decoder.Decode(gray, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8);
            if (result == null) return;
            // Validate the same signed/pinned protocol data that pairing will use. Do not launch QR URLs.
            var qr = System.Text.Json.JsonSerializer.Deserialize(result.Text, Ftx1.Protocol.ProtocolJson.Default.PairingQr);
            if (qr == null) return; Ftx1.Client.StationConnection.ValidateSettings(new(qr.Address, qr.Fingerprint, ""));
            completed = true; RunOnUiThread(() => { SetResult(global::Android.App.Result.Ok, new Intent().PutExtra("pairing", result.Text)); Finish(); });
        }
        catch (Exception) { /* Keep scanning non-pairing QR codes or incomplete camera frames. */ }
    }
    protected override void OnPause() { closed = true; session?.Close(); camera?.Close(); reader?.Close(); surface?.Dispose(); thread?.QuitSafely(); base.OnPause(); }
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surfaceTexture) => true;
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surfaceTexture, int width, int height) { }
    public void OnSurfaceTextureUpdated(SurfaceTexture surfaceTexture) { }
    sealed class Frames(QrActivity owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener { public void OnImageAvailable(ImageReader? source) { if (source != null) owner.Decode(source); } }
    sealed class Opened(QrActivity owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => owner.StartSession(camera);
        public override void OnDisconnected(CameraDevice camera) { camera.Close(); owner.RunOnUiThread(owner.Finish); }
        public override void OnError(CameraDevice camera, CameraError error) { camera.Close(); owner.RunOnUiThread(owner.Finish); }
    }
    sealed class Configured(QrActivity owner) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            if (owner.closed) { session.Close(); return; } owner.session = session;
            using var request = owner.camera!.CreateCaptureRequest(CameraTemplate.Preview)!; request.AddTarget(owner.surface!); request.AddTarget(owner.reader!.Surface!);
            session.SetRepeatingRequest(request.Build()!, null, owner.handler);
        }
        public override void OnConfigureFailed(CameraCaptureSession session) => owner.RunOnUiThread(owner.Finish);
    }
}
