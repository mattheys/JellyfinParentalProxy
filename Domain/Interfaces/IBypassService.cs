using System;
using System.Collections.Generic;
using System.Text;

namespace Domain.Interfaces;

public interface IBypassService
{
    public bool GetBypassState { get; }
    public void SetBypassState(bool state);
}
