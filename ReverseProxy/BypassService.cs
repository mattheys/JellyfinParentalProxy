using Domain.Interfaces;

namespace ReverseProxy;

public class BypassService : IBypassService
{
    private bool _bypass = false;
    public bool GetBypassState => _bypass;

    public void SetBypassState(bool state) => _bypass = state;
}
