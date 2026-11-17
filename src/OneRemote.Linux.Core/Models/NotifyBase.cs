using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace OneRemote.Linux.Core.Models;

public abstract class NotifyBase : INotifyPropertyChanged
{
    private bool _notifyEnabled = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetNotifyPropertyChangedEnabled(bool enabled)
    {
        _notifyEnabled = enabled;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (_notifyEnabled && propertyName != null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    protected bool SetAndNotifyIfChanged<T>(ref T backingField, T newValue, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(backingField, newValue))
            return false;
        backingField = newValue;
        RaisePropertyChanged(propertyName);
        return true;
    }
}
