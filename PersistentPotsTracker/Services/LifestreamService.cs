
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using PersistentPotsTracker.Models;
using System;

namespace PersistentPotsTracker.Services;

/// <summary>
/// Optional integration with Lifestream through Dalamud IPC.
/// </summary>
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
        try
        {
            busy = isBusy.InvokeFunc();
            return true;
        }
        catch
        {
            busy = false;
            return false;
        }
    }
    public bool TryTravel(
        PotTableRow row,
        string currentDatacenter,
        out string message)
    {
        string occultCommand = row.Zone switch
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

        if (row.Datacenter is not
            ("Aether" or "Primal" or "Crystal" or "Dynamis" or "Materia"))
        {
            message = "The selected tracker has an unsupported datacenter.";
            return false;
        }

        string command = string.Equals(
            row.Datacenter,
            currentDatacenter,
            StringComparison.OrdinalIgnoreCase)
            ? occultCommand
            : $"{row.Datacenter},{occultCommand}";

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
}
