using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PgrVoice;

public sealed record AudioDeviceOption(string Id, string Name, bool Available = true)
{
    public override string ToString() => Name;
}

internal sealed class AudioDeviceNotifications : IMMNotificationClient
{
    readonly Action<string?, bool> changed;
    public AudioDeviceNotifications(Action<string?, bool> changed) => this.changed = changed;
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed(newState == DeviceState.Active ? null : deviceId, false);
    public void OnDeviceAdded(string deviceId) => changed(null, false);
    public void OnDeviceRemoved(string deviceId) => changed(deviceId, false);
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) changed(null, true);
    }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) => changed(null, false);
}
