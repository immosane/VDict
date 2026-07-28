using System.ComponentModel;
using System.Runtime.CompilerServices;
using VDict.Core.Models;

namespace VDict.WinUI.Views.SettingsSections;

public sealed class DictionarySettingsListItem : INotifyPropertyChanged
{
    public DictionarySettingsListItem(DictionaryDefinition definition)
    {
        Definition = definition;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DictionaryDefinition Definition { get; private set; }

    public DictionaryId Id => Definition.Id;

    public string EffectiveDisplayName => Definition.EffectiveDisplayName;

    public void Update(DictionaryDefinition definition)
    {
        if (Definition == definition)
        {
            return;
        }

        string previousDisplayName = EffectiveDisplayName;
        Definition = definition;
        OnPropertyChanged(nameof(Definition));
        if (!previousDisplayName.Equals(EffectiveDisplayName, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(EffectiveDisplayName));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
