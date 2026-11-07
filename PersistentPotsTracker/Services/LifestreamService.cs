
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using PersistentPotsTracker.Models;
using System;

namespace PersistentPotsTracker.Services;

public sealed class LifestreamService
{
    private readonly ICallGateSubscriber<string, object> executeCommand;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly IPluginLog log;

    public LifestreamService(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    {
        executeCommand =
            pluginInterface.GetIpcSubscriber<string, object>(
                "Lifestream.ExecuteCommand");

        isBusy =
            pluginInterface.GetIpcSubscriber<bool>(
                "Lifestream.IsBusy");

        this.log = log;
    }

    public bool TryGetBusy(out bool busy)
    {
        busy = false;

        // Avoid invoking IPC when Lifestream is unavailable.
        if (!isBusy.HasFunction)
            return false;

        try
        {
            busy = isBusy.InvokeFunc();
            return true;
        }
        catch (Exception)
        {
            // This can be polled frequently while traveling.
            // Avoid filling the log with repeated IPC errors.
            busy = false;
            return false;
        }
    }

    public bool TryTravel(
        PotTableRow row,
        string currentDatacenter,
        string homeDatacenter,
        string homeWorld,
        out string message)
    {
        var occultCommand = row.Zone switch
        {
            "North Horn" => "occult",
            "South Horn" => "occult s",
            _ => string.Empty
        };

        if (occultCommand.Length == 0)
        {
            message =
                "The selected tracker has no known Horn destination.";
            return false;
        }

        // Only supported datacenter names can enter Lifestream commands.
        if (row.Datacenter is not
            ("Aether" or "Primal" or "Crystal" or "Dynamis" or "Materia"))
        {
            message =
                "The selected tracker has an unsupported datacenter.";
            return false;
        }

        if (!isBusy.HasFunction || !executeCommand.HasAction)
        {
            message =
                "Could not reach Lifestream. " +
                "Check that it is installed and enabled.";
            return false;
        }

        string command;

        if (string.Equals(
                row.Datacenter,
                currentDatacenter,
                StringComparison.OrdinalIgnoreCase))
        {
            // Already on the destination datacenter.
            command = occultCommand;
        }
        else if (string.Equals(
                     row.Datacenter,
                     homeDatacenter,
                     StringComparison.OrdinalIgnoreCase))
        {
            // Return directly to the player's home world.
            // Do not silently select another world if the name is invalid.
            if (!IsPlainWorldName(homeWorld))
            {
                message =
                    "Could not determine a valid home world " +
                    "for Lifestream travel.";
                return false;
            }

            command = $"{homeWorld},{occultCommand}";
        }
        else
        {
            // Travel to another datacenter.
            command = $"{row.Datacenter},{occultCommand}";
        }

        try
        {
            if (isBusy.InvokeFunc())
            {
                message =
                    "Lifestream is already busy. Try again later.";
                return false;
            }

            executeCommand.InvokeAction(command);

            log.Information(
                $"Lifestream travel requested: {command}");

            message =
                $"Sent travel request for {row.Datacenter} / {row.Zone}. " +
                "Check Lifestream for progress.";

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(
                ex,
                "Could not send travel request to Lifestream.");

            message =
                "Could not reach Lifestream. " +
                "Check that it is installed and enabled.";

            return false;
        }
    }

    // FFXIV world names use ASCII letters.
    // Reject delimiters or unexpected characters in IPC commands.
    private static bool IsPlainWorldName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        foreach (var c in name)
        {
            if (!((c >= 'A' && c <= 'Z') ||
                  (c >= 'a' && c <= 'z')))
            {
                return false;
            }
        }

        return true;
    }
}
