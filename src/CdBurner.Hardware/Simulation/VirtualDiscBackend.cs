namespace CdBurner.Hardware.Simulation;

/// <summary>
/// High-level facade combining a <see cref="VirtualDiscRecorder"/> and <see cref="VirtualBurnEngine"/>
/// to simulate full optical drive operations in test and non-hardware environments.
/// </summary>
public class VirtualDiscBackend : IDisposable
{
    /// <summary>
    /// Virtual drive recorder backend.
    /// </summary>
    public VirtualDiscRecorder Recorder { get; }

    /// <summary>
    /// Virtual burn/rip/erase engine.
    /// </summary>
    public VirtualBurnEngine Engine { get; }

    /// <summary>
    /// The medium currently inserted in the virtual drive.
    /// </summary>
    public VirtualDiscMedium? CurrentMedium => Recorder.CurrentMedium;

    public VirtualDiscBackend(VirtualDiscRecorder? recorder = null, VirtualBurnEngine? engine = null)
    {
        Recorder = recorder ?? new VirtualDiscRecorder();
        Engine = engine ?? new VirtualBurnEngine(Recorder);
        if (Engine.DefaultRecorder == null)
        {
            Engine.DefaultRecorder = Recorder;
        }
    }

    /// <summary>
    /// Inserts a virtual medium into the virtual drive.
    /// </summary>
    public void Mount(VirtualDiscMedium medium) => Recorder.InsertMedia(medium);

    /// <summary>
    /// Ejects the drive tray.
    /// </summary>
    public void Eject() => Recorder.EjectMedia();

    /// <summary>
    /// Closes the drive tray.
    /// </summary>
    public void CloseTray() => Recorder.CloseTray();

    public void Dispose()
    {
        Recorder.Dispose();
    }
}
