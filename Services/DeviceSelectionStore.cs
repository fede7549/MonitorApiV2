using MonitorPapa.Api.Models;

namespace MonitorPapa.Api.Services;

public class DeviceSelectionStore
{
    private readonly object _lock = new();
    private readonly DeviceSelectionState _state = new();

    public DeviceSelectionState GetSnapshot()
    {
        lock (_lock)
        {
            return new DeviceSelectionState
            {
                Oximetro = _state.Oximetro,
                Wt901 = _state.Wt901,
                Ring = _state.Ring
            };
        }
    }

    public void Update(DeviceSelectionState selection)
    {
        lock (_lock)
        {
            _state.Oximetro = selection.Oximetro;
            _state.Wt901 = selection.Wt901;
            _state.Ring = selection.Ring;
        }
    }

    public bool IsOximetroEnabled => GetSnapshot().Oximetro;
    public bool IsWt901Enabled => GetSnapshot().Wt901;
    public bool IsRingEnabled => GetSnapshot().Ring;
}
