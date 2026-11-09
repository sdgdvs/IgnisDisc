using System.Diagnostics;
using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Burning;

/// <summary>
/// Action to perform automatically after a burn or queue operation completes.
/// </summary>
public enum PostBurnAction
{
    /// <summary>
    /// Do nothing after burning completes.
    /// </summary>
    None = 0,

    /// <summary>
    /// Eject the optical disc tray.
    /// </summary>
    Eject = 1,

    /// <summary>
    /// Play a system notification sound or custom chime.
    /// </summary>
    PlaySound = 2,

    /// <summary>
    /// Eject the optical disc tray and play a notification sound.
    /// </summary>
    EjectAndPlaySound = 3,

    /// <summary>
    /// Shut down the host computer after burning completes.
    /// </summary>
    ShutDown = 4
}

/// <summary>An after-burn choice paired with the text shown for it in the app.</summary>
public sealed record PostBurnActionOption(PostBurnAction Action, string Label);

/// <summary>
/// Executes configured after-burn actions (<see cref="PostBurnAction"/>) safely without crashing a completed burn.
/// </summary>
public static class PostBurnActionExecutor
{
    /// <summary>
    /// Default sound player delegate invoked when <see cref="PostBurnAction.PlaySound"/> or
    /// <see cref="PostBurnAction.EjectAndPlaySound"/> is executed and no explicit callback is passed.
    /// </summary>
    public static Action? DefaultSoundPlayer { get; set; }

    /// <summary>
    /// Default shutdown initiator delegate invoked when <see cref="PostBurnAction.ShutDown"/> is executed
    /// and no explicit callback is passed. Defaults to safe no-op so unit tests never shut down the host machine.
    /// </summary>
    public static Action? DefaultShutdownInitiator { get; set; }

    /// <summary>Seconds Windows counts down before an after-burn shutdown; <c>shutdown /a</c> cancels it.</summary>
    public const int ShutdownDelaySeconds = 60;

    /// <summary>
    /// The command the app runs for <see cref="PostBurnAction.ShutDown"/>: <c>shutdown.exe /s /t 60</c> with a comment
    /// explaining how to cancel. Windows shows its own warning, so the user can still stop it.
    /// </summary>
    public static ProcessStartInfo CreateShutdownStartInfo()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add(ShutdownDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add($"IgnisDisc finished burning. Windows will shut down in {ShutdownDelaySeconds} seconds. To cancel, run: shutdown /a");
        return psi;
    }

    /// <summary>Plain-English names for the after-burn choices shown in the app.</summary>
    public static string GetDisplayName(PostBurnAction action) => action switch
    {
        PostBurnAction.None => "Do nothing",
        PostBurnAction.Eject => "Eject the disc",
        PostBurnAction.PlaySound => "Play a sound",
        PostBurnAction.EjectAndPlaySound => "Eject and play a sound",
        PostBurnAction.ShutDown => $"Shut down the PC ({ShutdownDelaySeconds} s warning)",
        _ => action.ToString()
    };

    /// <summary>
    /// Executes the specified post-burn action against the recorder and optional callbacks.
    /// </summary>
    /// <param name="action">The post-burn action to execute.</param>
    /// <param name="recorder">The optical recorder backend to eject if ejection is requested.</param>
    /// <param name="soundPlayer">Optional sound player delegate. When null, plays default Windows asterisk sound.</param>
    /// <param name="shutdownInitiator">Optional shutdown delegate. When null, invokes <see cref="DefaultShutdownInitiator"/>.</param>
    public static void Execute(
        PostBurnAction action,
        IDiscRecorderBackend? recorder,
        Action? soundPlayer = null,
        Action? shutdownInitiator = null)
    {
        if (action == PostBurnAction.None)
        {
            return;
        }

        // When Eject or EjectAndPlaySound is set and recorder != null, calls recorder.EjectMedia() safely
        // (catching any hardware exception so post-burn never crashes a completed burn).
        if ((action == PostBurnAction.Eject || action == PostBurnAction.EjectAndPlaySound) && recorder != null)
        {
            try
            {
                recorder.EjectMedia();
            }
            catch
            {
                // Safely ignore hardware tray ejection exceptions
            }
        }

        // When PlaySound or EjectAndPlaySound is set, invokes soundPlayer (or SystemSounds.Asterisk on Windows)
        if (action == PostBurnAction.PlaySound || action == PostBurnAction.EjectAndPlaySound)
        {
            var player = soundPlayer ?? DefaultSoundPlayer;
            if (player != null)
            {
                try
                {
                    player();
                }
                catch
                {
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                try
                {
                    System.Media.SystemSounds.Asterisk.Play();
                }
                catch
                {
                }
            }
        }

        // When ShutDown is set, invokes shutdownInitiator (defaulting to safe injectable callback)
        if (action == PostBurnAction.ShutDown)
        {
            var initiator = shutdownInitiator ?? DefaultShutdownInitiator;
            if (initiator != null)
            {
                try
                {
                    initiator();
                }
                catch
                {
                }
            }
        }
    }
}
