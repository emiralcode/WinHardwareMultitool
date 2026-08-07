using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using WinHardwareMultitool.Models;

namespace WinHardwareMultitool.Services;

/// <summary>
/// Synthetic load generators for CPU, GPU and Disk. Every run is gated by
/// <see cref="SafetyGuardService.IsSafeToStart"/> and registers its CancellationTokenSource
/// with the Safety Guard so a thermal emergency aborts it immediately, regardless of what
/// the caller does with the token afterwards.
/// </summary>
public sealed class StressTestService
{
    private readonly SafetyGuardService _safetyGuard;

    public bool IsRunning { get; private set; }

    public event EventHandler<string>? LogMessage;
    public event EventHandler<StressTestType>? TestStarted;
    public event EventHandler<StressTestType>? TestCompleted;
    public event EventHandler<(StressTestType Type, string Reason)>? TestAborted;

    public StressTestService(SafetyGuardService safetyGuard)
    {
        _safetyGuard = safetyGuard;
    }

    public async Task RunAsync(StressTestType type, TimeSpan? duration, CancellationToken userToken)
    {
        if (IsRunning)
        {
            LogMessage?.Invoke(this, "Zaten çalışan bir test var, önce onu durdurun.");
            return;
        }

        if (!_safetyGuard.IsSafeToStart(out var reason))
        {
            LogMessage?.Invoke(this, $"Test başlatılamadı: {reason}");
            TestAborted?.Invoke(this, (type, reason ?? "Güvenlik"));
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        _safetyGuard.RegisterActiveTest(linkedCts);
        if (duration is { } d)
            linkedCts.CancelAfter(d);

        IsRunning = true;
        TestStarted?.Invoke(this, type);
        LogMessage?.Invoke(this, $"{type} stres testi başlatıldı" + (duration is null ? "." : $" (süre: {duration.Value.TotalSeconds:0} sn)."));

        try
        {
            Task work = type switch
            {
                StressTestType.Cpu => RunCpuAsync(linkedCts.Token),
                StressTestType.Gpu => RunGpuAsync(linkedCts.Token),
                StressTestType.Disk => RunDiskAsync(linkedCts.Token),
                _ => Task.CompletedTask
            };

            await work.ConfigureAwait(false);

            LogMessage?.Invoke(this, $"{type} stres testi tamamlandı.");
            TestCompleted?.Invoke(this, type);
        }
        catch (OperationCanceledException)
        {
            string abortReason = _safetyGuard.CurrentLevel == SafetyLevel.Critical
                ? "Termal güvenlik tetiklendi"
                : "Kullanıcı tarafından durduruldu";
            LogMessage?.Invoke(this, $"{type} stres testi iptal edildi: {abortReason}");
            TestAborted?.Invoke(this, (type, abortReason));
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, $"{type} stres testi hata ile sonlandı: {ex.Message}");
            TestAborted?.Invoke(this, (type, ex.Message));
        }
        finally
        {
            _safetyGuard.UnregisterActiveTest();
            IsRunning = false;
        }
    }

    // ---------------------------------------------------------------- CPU

    private static Task RunCpuAsync(CancellationToken token)
    {
        int threadCount = Math.Max(1, Environment.ProcessorCount);
        var tasks = new Task[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            int seed = i;
            tasks[i] = Task.Factory.StartNew(
                () => CpuBurn(seed, token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        return Task.WhenAll(tasks);
    }

    private static void CpuBurn(int seed, CancellationToken token)
    {
        const int size = 96;
        var a = new double[size, size];
        var b = new double[size, size];
        var result = new double[size, size];
        var rnd = new Random(seed ^ Environment.TickCount);

        for (int i = 0; i < size; i++)
            for (int j = 0; j < size; j++)
            {
                a[i, j] = rnd.NextDouble();
                b[i, j] = rnd.NextDouble();
            }

        while (!token.IsCancellationRequested)
        {
            Array.Clear(result, 0, result.Length);

            for (int i = 0; i < size; i++)
                for (int k = 0; k < size; k++)
                {
                    double aik = a[i, k];
                    for (int j = 0; j < size; j++)
                        result[i, j] += aik * b[k, j];
                }

            // Feed the result back in so the JIT can't prove the computation is dead and elide it.
            a[0, 0] = (result[size - 1, size - 1] % 1000.0) + 1.0;
        }
    }

    // ---------------------------------------------------------------- GPU

    /// <summary>
    /// Drives WPF's Direct3D-backed Viewport3D renderer as fast as possible on a dedicated
    /// STA thread. This is a lightweight proxy for real GPU load - a true compute-shader-based
    /// stress test would require an extra dependency (e.g. Vortice.Windows) which this project
    /// deliberately avoids to keep the tool dependency-light.
    /// </summary>
    private static Task RunGpuAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() => GpuRenderLoop(token, tcs))
        {
            IsBackground = true,
            Name = "GpuStressRenderThread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private static void GpuRenderLoop(CancellationToken token, TaskCompletionSource tcs)
    {
        System.Windows.Window? window = null;
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;

            window = new System.Windows.Window
            {
                Width = 512,
                Height = 512,
                WindowStyle = System.Windows.WindowStyle.None,
                ShowInTaskbar = false,
                Left = -3000,
                Top = -3000,
                Background = Brushes.Black,
                Content = BuildStressViewport(out var modelGroup)
            };
            window.Show();

            double angle = 0;
            void OnRendering(object? s, EventArgs e)
            {
                if (token.IsCancellationRequested)
                {
                    CompositionTarget.Rendering -= OnRendering;
                    window?.Close();
                    dispatcher.InvokeShutdown();
                    return;
                }

                angle += 2.0;
                modelGroup.Transform = new Transform3DGroup
                {
                    Children =
                    {
                        new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), angle)),
                        new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), angle * 0.7))
                    }
                };
            }

            CompositionTarget.Rendering += OnRendering;

            Dispatcher.Run();
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            try { window?.Close(); } catch { /* thread is tearing down anyway */ }
        }
    }

    private static Viewport3D BuildStressViewport(out Model3DGroup modelGroup)
    {
        var viewport = new Viewport3D
        {
            Camera = new PerspectiveCamera
            {
                Position = new Point3D(0, 0, 10),
                LookDirection = new Vector3D(0, 0, -1),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 60
            }
        };

        modelGroup = new Model3DGroup();
        modelGroup.Children.Add(new AmbientLight(Colors.DimGray));
        modelGroup.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -1)));

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))));
        material.Children.Add(new SpecularMaterial(Brushes.White, 40));

        var sphereMesh = CreateSphereMesh(slices: 32, stacks: 32, radius: 0.6);

        const int grid = 3;
        for (int x = 0; x < grid; x++)
            for (int y = 0; y < grid; y++)
                for (int z = 0; z < grid; z++)
                {
                    var geometry = new GeometryModel3D(sphereMesh, material)
                    {
                        Transform = new TranslateTransform3D(
                            (x - grid / 2.0) * 1.8,
                            (y - grid / 2.0) * 1.8,
                            (z - grid / 2.0) * 1.8)
                    };
                    modelGroup.Children.Add(geometry);
                }

        viewport.Children.Add(new ModelVisual3D { Content = modelGroup });
        return viewport;
    }

    private static MeshGeometry3D CreateSphereMesh(int slices, int stacks, double radius)
    {
        var mesh = new MeshGeometry3D();

        for (int stack = 0; stack <= stacks; stack++)
        {
            double phi = Math.PI * stack / stacks;
            for (int slice = 0; slice <= slices; slice++)
            {
                double theta = 2 * Math.PI * slice / slices;
                double x = radius * Math.Sin(phi) * Math.Cos(theta);
                double y = radius * Math.Cos(phi);
                double z = radius * Math.Sin(phi) * Math.Sin(theta);
                mesh.Positions.Add(new Point3D(x, y, z));
                mesh.Normals.Add(new Vector3D(x, y, z));
            }
        }

        int ring = slices + 1;
        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                int p0 = stack * ring + slice;
                int p1 = p0 + ring;
                int p2 = p0 + 1;
                int p3 = p1 + 1;

                mesh.TriangleIndices.Add(p0);
                mesh.TriangleIndices.Add(p1);
                mesh.TriangleIndices.Add(p2);

                mesh.TriangleIndices.Add(p2);
                mesh.TriangleIndices.Add(p1);
                mesh.TriangleIndices.Add(p3);
            }
        }

        return mesh;
    }

    // ---------------------------------------------------------------- Disk

    private async Task RunDiskAsync(CancellationToken token)
    {
        string path = Path.Combine(Path.GetTempPath(), $"whm_stress_{Guid.NewGuid():N}.dat");
        const int fileSizeMb = 1024;
        const int bufferSizeBytes = 1024 * 1024;
        var buffer = new byte[bufferSizeBytes];
        Random.Shared.NextBytes(buffer);
        var sw = new Stopwatch();

        try
        {
            sw.Restart();
            await using (var fs = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSizeBytes, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                long totalBytes = (long)fileSizeMb * 1024 * 1024;
                long written = 0;
                while (written < totalBytes)
                {
                    token.ThrowIfCancellationRequested();
                    await fs.WriteAsync(buffer, token).ConfigureAwait(false);
                    written += buffer.Length;
                }
            }
            sw.Stop();
            LogMessage?.Invoke(this, $"Disk yazma hızı (ortalama): {fileSizeMb / sw.Elapsed.TotalSeconds:0.#} MB/s");

            token.ThrowIfCancellationRequested();

            sw.Restart();
            await using (var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None,
                bufferSizeBytes, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                int readBytes;
                do
                {
                    token.ThrowIfCancellationRequested();
                    readBytes = await fs.ReadAsync(buffer, token).ConfigureAwait(false);
                } while (readBytes > 0);
            }
            sw.Stop();
            LogMessage?.Invoke(this, $"Disk okuma hızı (ortalama): {fileSizeMb / sw.Elapsed.TotalSeconds:0.#} MB/s");
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Geçici test dosyası silinemedi ({path}): {ex.Message}");
            }
        }
    }
}
