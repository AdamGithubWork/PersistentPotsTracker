using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using PersistentPotsTracker.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PersistentPotsTracker.Services;

public sealed class LifestreamService
{
    private static readonly Dictionary<string, string> OccultCommands = new()
    {
        ["North Horn"] = "occult",
        ["South Horn"] = "occult s",
    };

    private readonly ICallGateSubscriber<string, object> executeCommand;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly IPluginLog log;

    public LifestreamService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        executeCommand = pluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        this.log = log;
    }

    public bool IsAvailable => isBusy.HasFunction && executeCommand.HasAction;

    public bool TryGetBusy(out bool busy)
    {
        busy = false;

        if (!isBusy.HasFunction)
            return false;

        try
        {
            busy = isBusy.InvokeFunc();
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Lifestream busy check failed.");
            return false;
        }
    }

    public bool TryTravel(PotTableRow row, string currentDatacenter, string homeDatacenter, string homeWorld, out string message)
    {
        if (!OccultCommands.TryGetValue(row.Zone, out var occultCommand))
        {
            message = "Unknown Horn destination.";
            return false;
        }

        if (row.Datacenter is not ("Aether" or "Primal" or "Crystal" or "Dynamis" or "Materia"))
        {
            message = "Unsupported datacenter.";
            return false;
        }

        if (!IsAvailable)
        {
            message = "Lifestream isn't available. Make sure it's enabled.";
            return false;
        }

        // Same DC: run the occult command from wherever we are.
        // Home DC: go through the home world so we don't end up on a random one.
        // Other DC: give Lifestream the DC and let it pick the world.
        string command;

        if (string.Equals(row.Datacenter, currentDatacenter, StringComparison.OrdinalIgnoreCase))
        {
            command = occultCommand;
        }
        else if (string.Equals(row.Datacenter, homeDatacenter, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsValidWorldName(homeWorld))
            {
                message = "No valid home world set.";
                return false;
            }

            command = $"{homeWorld},{occultCommand}";
        }
        else
        {
            command = $"{row.Datacenter},{occultCommand}";
        }

        if (!TryGetBusy(out var busy))
        {
            message = "Couldn't check whether Lifestream is busy.";
            return false;
        }

        if (busy)
        {
            message = "Lifestream is busy.";
            return false;
        }

        try
        {
            executeCommand.InvokeAction(command);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Lifestream rejected command '{command}'.");
            message = "Lifestream didn't accept the travel command.";
            return false;
        }

        log.Information($"Sent to Lifestream: {command}");
        message = $"Travelling to {row.Zone} ({row.Datacenter})";
        return true;
    }

    // The world name goes straight into Lifestream's command string, so keep it
    // to letters. A comma or space would change what gets executed.
    private static bool IsValidWorldName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.All(char.IsAsciiLetter);
}
