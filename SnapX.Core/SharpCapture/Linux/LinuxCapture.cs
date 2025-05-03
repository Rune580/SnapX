using System.Collections.Concurrent;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using PipeWireSharp;
using PipeWireSharp.PipeWire;
using PipeWireSharp.PipeWire.Streams;
using PipeWireSharp.Spa;
using PipeWireSharp.Spa.Enums;
using PipeWireSharp.Spa.Pods;
using PipeWireSharp.Spa.Pods.Object;
using PipeWireSharp.Spa.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SnapX.Core.SharpCapture.Linux.DBus;
using Tmds.DBus;
using Tmds.DBus.Protocol;
using Stream = PipeWireSharp.PipeWire.Streams.Stream;

namespace SnapX.Core.SharpCapture.Linux;

public class LinuxCapture : BaseCapture
{
    private FormatPodObject? formatObject;

    public override async Task<Image?> CaptureFullscreen()
    {
        // if (LinuxAPI.IsWayland()) return await TakeScreenshotWithPortal();

        await StartRecording();

        // if (!IsCompositorKwin) return await TakeScreenshotWithPortal();
        // Todo: replace try catch with method that checks for valid kwin permissions.
        try
        {
            // return await TakeScreenshotWithKwin();
        }
        catch (Exception e)
        {
            // Fallback to portal method.
        }

        // return await TakeScreenshotWithPortal();

        throw new NotImplementedException();
    }

    private static async Task<Image> TakeScreenshotWithPortal()
    {
        var connection = new Connection(Address.Session!);
        await connection.ConnectAsync().ConfigureAwait(false);
        var desktop = new DesktopService(connection, "org.freedesktop.portal.Desktop");
        // var access = new DesktopService(connection, "org.freedesktop.access");
        var screenshot = desktop.CreateScreenshot("/org/freedesktop/portal/desktop");
        var options = new Dictionary<string, VariantValue>()
        {
            // { "interactive", true }
        };
        var timeoutTask = Task.Delay(10000);
        var portalResponse = connection.Call(() => screenshot.ScreenshotAsync("", options));

        var completedTask = await Task.WhenAny(portalResponse, timeoutTask);

        if (completedTask == timeoutTask)
        {
            throw new TimeoutException("Call to org.freedesktop.portal.Desktop Screenshot timed out. Please try again.");
        }
        var Response = await portalResponse;
        var uri = new Uri(Response.Results["uri"].GetString());
        var fileURL = Uri.UnescapeDataString(uri.LocalPath);
        var img = await Image.LoadAsync(fileURL);
        _ = Task.Run(() => File.Delete(fileURL));

        return img;
    }

    // A significantly faster solution for screen capturing on KDE Wayland over FreeDesktop Portals.
    //
    // Instead of creating/contributing a new wayland protocol or using an existing wayland protocol for screen capturing,
    // KWin provides a special dbus interface `org.kde.KWin.ScreenShot2` for taking screenshots without prompting the user. This is meant for their in-house screenshot app `Spectacle`.
    // However, this interface *can* be used by other apps, as long as you follow a few rules:
    //   1. There must be a .desktop file in a privileged location e.g., /usr/share/applications/
    //   2. The .desktop entry `Exec` *must* point to a bin located in a privileged location e.g., `Exec=/usr/bin/snapx`
    //   3. The .desktop file *must* contain the following entry: `X-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2`
    //
    // If all these rules are followed, KWin will allow SnapX to take privileged, unprompted screenshots on wayland.
    // Interface Documentation: https://github.com/KDE/kwin/blob/master/src/plugins/screenshot/org.kde.KWin.ScreenShot2.xml
    private static async Task<Image> TakeScreenshotWithKwin()
    {
        var connection = new Connection(Address.Session!);
        await connection.ConnectAsync().ConfigureAwait(false);
        var screenShotService = new ScreenShot2Service(connection, "org.kde.KWin.ScreenShot2");
        var screenshot = screenShotService.CreateScreenShot2("/org/kde/KWin/ScreenShot2");
        var options = new Dictionary<string, VariantValue>()
        {
            // { "include-cursor", false },
            // { "native-resolution", false },
        };

        var tempFile = Path.GetTempFileName();
        var fileHandle = File.OpenHandle(tempFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        var timeoutTask = Task.Delay(10000);
        var kwinResponse = screenshot.CaptureWorkspaceAsync(options, fileHandle);

        var completedTask = await Task.WhenAny(kwinResponse, timeoutTask);
        if (completedTask == timeoutTask)
        {
            throw new TimeoutException("Call to org.kde.KWin.ScreenShot2 Screenshot timed out. Please try again.");
        }

        var result = await kwinResponse;
        var expectedSize = result.Stride * (long)result.Height;

        while (new FileInfo(tempFile).Length < expectedSize)
        {
            await Task.Delay(100);
            // Todo Timeout
        }

        var image = await QImage.LoadAsync(tempFile, result);
        _ = Task.Run(() => File.Delete(tempFile));

        return image;
    }

    private static Image CropFullscreenScreenshotToBounds(Rectangle bounds, Image img)
    {
        var cropRectangle = new Rectangle(
            Math.Max(0, bounds.X),
            Math.Max(0, bounds.Y),
            Math.Min(img.Width - bounds.X, bounds.Width),
            Math.Min(img.Height - bounds.Y, bounds.Height)
        );

        img.Mutate(x => x.Crop(cropRectangle));

        return img;
    }
    public override async Task<Image?> CaptureScreen(Rectangle bounds)
    {
        // TODO: Implement pure X11 screenshotting instead of using portal
        // if (LinuxAPI.IsWayland())
        // {
        var syncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);


        var fullscreenImage = await CaptureFullscreen().ConfigureAwait(false);
        Console.WriteLine($"{fullscreenImage.Width}x{fullscreenImage.Height} {fullscreenImage.Configuration.ImageFormats}");
        var croppedImage = CropFullscreenScreenshotToBounds(bounds, fullscreenImage);
        Console.WriteLine($"{croppedImage.Width}x{croppedImage.Height} {croppedImage.Configuration.ImageFormats}");
        SynchronizationContext.SetSynchronizationContext(syncContext);
        return croppedImage;
        // }

        // return LinuxAPI.TakeScreenshotWithX11(screen);
    }

    private ConcurrentQueue<PipeWireFrame> _frameBuffer = new();
    private int _framesRemaining = 480;

    public async Task StartRecording()
    {
        var connection = new Connection(Address.Session!);
        await connection.ConnectAsync().ConfigureAwait(false);
        var desktop = new DesktopService(connection, "org.freedesktop.portal.Desktop");

        var screencast = desktop.CreateScreenCast("/org/freedesktop/portal/desktop");

        var handleToken = NewHandleToken();
        var sessionHandleToken = NewHandleToken();

        var createSessionOptions = new Dictionary<string, VariantValue>
        {
            { "handle_token", handleToken },
            { "session_handle_token", sessionHandleToken }
        };
        var response = await connection.Call(() => screencast.CreateSessionAsync(createSessionOptions));

        if (!response.Results.TryGetValue("session_handle", out var sessionHandle))
        {
            throw new Exception("Failed to create ScreenCast session");
        }

        var session = new Session(desktop, sessionHandle.GetString());
        PipeWireSharpLib.Init();

        var mainLoop = new MainLoop();

        try
        {
            // Possible values: https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html#org-freedesktop-portal-screencast-selectsources
            var selectSourcesOptions = new Dictionary<string, VariantValue>
            {
                { "handle_token", NewHandleToken() },
                { "types", (uint)(1 | 2 | 4) },
                { "multiple", false },
                { "cursor_mode", (uint)2 },
            };
            await screencast.SelectSourcesAsync(session.Path, selectSourcesOptions);

            var startOptions = new Dictionary<string, VariantValue>
            {
                { "handle_token", NewHandleToken() }
            };
            // TODO: Set `parentWindow`
            var streams = await connection.Call(() => screencast.StartAsync(session.Path, "", startOptions));

            var pipewireRemoteOptions = new Dictionary<string, VariantValue>();
            var pipewireRemote = await screencast.OpenPipeWireRemoteAsync(session.Path, pipewireRemoteOptions);

            if (pipewireRemote is null)
                throw new InvalidOperationException("Failed to open PipeWire remote!");

            var streamData = streams.Results["streams"].GetArray<VariantValue>();
            var nodeId = streamData[0].GetItem(0).GetUInt32();

            var context = new Context(mainLoop);
            var core = context.ConnectFd(pipewireRemote);

            var streamProperties = new PwProperties();

            streamProperties.Insert(PwPropertyKey.MEDIA_TYPE, "Video");
            streamProperties.Insert(PwPropertyKey.MEDIA_CATEGORY, "Capture");
            streamProperties.Insert(PwPropertyKey.MEDIA_ROLE, "Screen");

            var stream = new Stream(core, "snapx-capture-stream", streamProperties);

            var builder = stream.AddListener()
                .OnStateChanged(StateChangedEvent)
                .OnParamChanged(ParamChangedEvent)
                .OnProcess(ProcessEvent);

            var listener = builder.Register();

            var videoParams = new VideoParameters
            {
                Formats = { SpaVideoFormat.Rgb, SpaVideoFormat.Rgba, SpaVideoFormat.Bgr, SpaVideoFormat.Bgra },
                PreferredSize = new SpaRectangle
                {
                    Width = 2560,
                    Height = 1440
                },
                MinSize = new SpaRectangle
                {
                    Width = 1,
                    Height = 1
                },
                MaxSize = new SpaRectangle
                {
                    Width = 10240,
                    Height = 5760
                },
                PreferredFrameRate = new SpaFraction
                {
                    Numerator = 30,
                    Denominator = 1
                },
                MinFrameRate = new SpaFraction
                {
                    Numerator = 0,
                    Denominator = 1
                },
                MaxFrameRate = new SpaFraction
                {
                    Numerator = 480,
                    Denominator = 1
                }
            };

            stream.Connect(Direction.Input, nodeId, StreamFlags.AutoConnect | StreamFlags.MapBuffers, videoParams);

            ThreadPool.QueueUserWorkItem(CreateVideo);

            mainLoop.Run();
        }
        finally
        {
            await session.CloseAsync();
        }
    }

    private void StateChangedEvent(Stream streamRef, StreamState oldState, StreamState newState, string msg)
    {
        Console.WriteLine($"State Changed: {oldState} -> {newState}");
        if (!string.IsNullOrEmpty(msg))
            Console.WriteLine($"Got Error: {msg}");
    }

    void ParamChangedEvent(Stream streamRef, uint id, PodValue param)
    {
        Console.WriteLine("Param Changed!");
        Console.WriteLine(param.ToString());

        if (id == (uint)SpaParamType.Format)
        {
            formatObject = param;

            streamRef.UpdateParams(new StreamParameters());
        }
    }

    private void ProcessEvent(Stream stream)
    {
        if (_framesRemaining <= 0)
            return;

        var pwBuffer = stream.DequeueBuffer();

        if (formatObject is null)
        {
            Console.WriteLine("uhhh no?");
            return;
        }

        var size = formatObject.VideoSize!.Value;

        foreach (var bufferData in pwBuffer.Buffer.Data)
        {
            var frameData = bufferData.GetFrameData();

            _frameBuffer.Enqueue(new PipeWireFrame(frameData, (int)size.Width, (int)size.Height));
            _framesRemaining--;

            if (_framesRemaining <= 0)
            {
                break;
            }
        }

        stream.QueueBuffer(pwBuffer);
    }

    private IEnumerable<IVideoFrame> GetFrames()
    {
        while (_frameBuffer.TryDequeue(out var frame))
        {
            yield return frame;
        }
    }

    private void CreateVideo(object? State)
    {
        while (_framesRemaining > 0)
        {
            Thread.Sleep(100);
        }

        var videosSource = new RawVideoPipeSource(GetFrames())
        {
            FrameRate = 60,
        };

        FFMpegArguments
            .FromPipeInput(videosSource)
            .OutputToFile("/home/rune/Desktop/test_video.mp4", true, options =>
            {
                options.WithVideoCodec(VideoCodec.LibX264);
            })
            .ProcessSynchronously(ffMpegOptions: new FFOptions()
            {
                LogLevel = FFMpegLogLevel.Debug
            });
    }

    private static string NewHandleToken()
    {
        var guid = Guid.NewGuid().ToString();
        return $"snapx_{guid.Replace("-", "")}";
    }

    private static bool IsCompositorKwin => Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland" && Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") == "KDE";

    private class PipeWireFrame : IVideoFrame
    {
        private readonly byte[] _data;

        public PipeWireFrame(byte[] data, int width, int height)
        {
            _data = data;
            Width = width;
            Height = height;
        }

        public void Serialize(System.IO.Stream pipe)
        {
            pipe.Write(_data);
        }

        public Task SerializeAsync(System.IO.Stream pipe, CancellationToken token)
        {
            var task = pipe.WriteAsync(_data, token);
            return task.AsTask();
        }

        public int Width { get; set; }
        public int Height { get; set; }
        public string Format => "bgra";
    }
}
