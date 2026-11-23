using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    internal static class CommandDispatch
    {
        internal static async Task<CommandDelivery> SendAsync(AndroidBridge bridge, string serial,
            TabletControlMode mode, VoiceCommand command, bool observeChart, CancellationToken token,
            string preparedChart = null, Action onAcknowledged = null)
        {
            bool songChange = command.Id == "next" || command.Id == "previous";
            bool swipe = command.Id == "scroll-up" || command.Id == "scroll-down" ||
                (songChange && mode == TabletControlMode.SongSwipes);
            bool observe = observeChart && songChange;
            string before = preparedChart;
            string note = observe && !swipe && before == null ? "The starting chart could not be identified." : null;

            token.ThrowIfCancellationRequested();
            long started = Stopwatch.GetTimestamp();
            string message;
            string shortcut = mode == TabletControlMode.FastSongShortcuts && songChange
                ? VoiceCommands.FastSongShortcutFor(command.Id) : command.Shortcut;
            if (!String.IsNullOrEmpty(shortcut))
            {
                message = await bridge.SendShortcutAsync(serial, VoiceShortcut.Parse(shortcut), token);
                if (songChange && mode == TabletControlMode.FastSongShortcuts)
                    message = command.Title + " sent (" + shortcut + ").";
            }
            else if (swipe && observe)
                message = await bridge.SendObservedSwipeAsync(serial, command.Id, identity => before = identity, token);
            else if (swipe)
                message = await bridge.SendSwipeActionAsync(serial, command.Id, token);
            else if (songChange && mode == TabletControlMode.ArrowShortcuts)
                message = await bridge.SendKeyboardActionAsync(serial, command.Id, token);
            else if (songChange)
                message = await bridge.SendActionAsync(serial, command.Id, token);
            else
                throw new InvalidOperationException("Assign this action a shortcut in the command library first.");
            long acknowledged = Stopwatch.GetTimestamp();
            if (onAcknowledged != null) onAcknowledged();
            long? observed = null;
            if (observe && before != null)
            {
                try
                {
                    if (await bridge.WaitForChartChangeAsync(serial, before, command.Id, token))
                        observed = Stopwatch.GetTimestamp();
                    else note = "The tablet did not show the expected adjacent chart during the observation window.";
                }
                catch (OperationCanceledException) { note = "Observation canceled after Android accepted the command."; }
                catch (Exception) { note = "Android accepted the command. The chart could not be read afterward."; }
            }
            return new CommandDelivery(message, started, acknowledged, observed, note);
        }
    }
}
