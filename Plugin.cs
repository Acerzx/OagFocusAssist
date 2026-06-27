using NINA.Plugin;
using NINA.Plugin.Interfaces;                    // ← ДОБАВЛЕНО
using NINA.Profile.Interfaces;
using OagFocusAssist.Properties;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;

namespace OagFocusAssist;

[Export(typeof(IPluginManifest))]
public class Plugin : PluginBase, INotifyPropertyChanged
{

    [ImportingConstructor]
    public Plugin(IProfileService profileService)
    {
        // Миграция настроек при обновлении плагина
        if (Settings.Default.UpgradeSettings)
        {
            Settings.Default.Upgrade();
            Settings.Default.UpgradeSettings = false;
            Settings.Default.Save();
        }

        // Инициализация дефолтных значений (один раз)
        if (!Settings.Default.Initialized)
        {
            Settings.Default.DemoMode = true;
            Settings.Default.ExposureTime = 0.5;
            Settings.Default.Initialized = true;
            Settings.Default.Save();
        }
    }

    // === Настройки для UI N.I.N.A. (Options → Plugins → OAG Manual Focus Assist) ===

    [DisplayName("Demo Mode (Демо-режим)")]
    [Description("Генерация синтетической V-кривой HFR. Отключите для работы с реальной камерой.")]
    public bool DemoMode
    {
        get => Settings.Default.DemoMode;
        set
        {
            if (Settings.Default.DemoMode != value)
            {
                Settings.Default.DemoMode = value;
                Settings.Default.Save();
                RaisePropertyChanged();
            }
        }
    }

    [DisplayName("Default Exposure (Экспозиция по умолчанию, сек)")]
    [Description("Экспозиция для съёмки кадров фокусировки.")]
    public double DefaultExposure
    {
        get => Settings.Default.ExposureTime;
        set
        {
            double clamped = Math.Clamp(value, 0.01, 300);
            if (Math.Abs(Settings.Default.ExposureTime - clamped) > 1e-9)
            {
                Settings.Default.ExposureTime = clamped;
                Settings.Default.Save();
                RaisePropertyChanged();
            }
        }
    }

    // INotifyPropertyChanged
    public event PropertyChangedEventHandler PropertyChanged;

    protected void RaisePropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}