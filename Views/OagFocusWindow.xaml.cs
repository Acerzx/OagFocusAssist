using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using OagFocusAssist.Services;
using OagFocusAssist.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OagFocusAssist.Views;

public partial class OagFocusWindow : Window
{
    private readonly OagFocusViewModel _viewModel;
    private Storyboard _currentGraphAnimation;
    private Storyboard _currentButtonAnimation;

    public OagFocusWindow(
        IOagFocusService focusService,
        ICameraMediator camera,
        IImagingMediator imaging,
        IProfileService profileService)
    {
        try
        {
            InitializeComponent();
            _viewModel = new OagFocusViewModel(focusService, camera, imaging, profileService);
            DataContext = _viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateAnimations();
        }
        catch (Exception ex)
        {
            Logger.Error($"OagFocusWindow: {ex}");
            throw;
        }
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OagFocusViewModel.IsOptimalState)
            or nameof(OagFocusViewModel.IsDegradedState))
        {
            UpdateAnimations();
        }
    }

    private void UpdateAnimations()
    {
        _currentGraphAnimation?.Stop(GraphBorder);
        _currentButtonAnimation?.Stop();

        GraphBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        PlayButton.RenderTransform = new ScaleTransform(1, 1);
        FinishButton.RenderTransform = new ScaleTransform(1, 1);

        if (_viewModel.IsOptimalState)
        {
            // Зелёная пульсация + акцент на Finish (пора завершать)
            _currentGraphAnimation = CreateGraphPulseAnimation(Color.FromRgb(0x10, 0xB9, 0x81));
            _currentButtonAnimation = CreateButtonPulseAnimation(FinishButton);
            _currentGraphAnimation.Begin(GraphBorder, isControllable: true);
            _currentButtonAnimation.Begin(FinishButton, isControllable: true);
        }
        else if (_viewModel.IsDegradedState)
        {
            // Красная пульсация + акцент на Play (нужна новая серия после корректировки)
            _currentGraphAnimation = CreateGraphPulseAnimation(Color.FromRgb(0xF8, 0x71, 0x71));
            _currentButtonAnimation = CreateButtonPulseAnimation(PlayButton);
            _currentGraphAnimation.Begin(GraphBorder, isControllable: true);
            _currentButtonAnimation.Begin(PlayButton, isControllable: true);
        }
    }

    private Storyboard CreateGraphPulseAnimation(Color pulseColor)
    {
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        var colorAnim = new ColorAnimationUsingKeyFrames();
        Storyboard.SetTarget(colorAnim, GraphBorder);
        Storyboard.SetTargetProperty(colorAnim, new PropertyPath("(Border.BorderBrush).(SolidColorBrush.Color)"));

        var baseColor = Color.FromRgb(0x3F, 0x3F, 0x46);

        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(pulseColor, TimeSpan.FromSeconds(0)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(pulseColor, TimeSpan.FromSeconds(0.4)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(baseColor, TimeSpan.FromSeconds(1.0)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(baseColor, TimeSpan.FromSeconds(1.4)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(pulseColor, TimeSpan.FromSeconds(2.0)));

        sb.Children.Add(colorAnim);
        return sb;
    }

    private Storyboard CreateButtonPulseAnimation(UIElement target)
    {
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        var animX = new DoubleAnimation(1.0, 1.04, TimeSpan.FromSeconds(0.7)) { AutoReverse = true };
        Storyboard.SetTarget(animX, target);
        Storyboard.SetTargetProperty(animX, new PropertyPath("RenderTransform.ScaleX"));

        var animY = new DoubleAnimation(1.0, 1.04, TimeSpan.FromSeconds(0.7)) { AutoReverse = true };
        Storyboard.SetTarget(animY, target);
        Storyboard.SetTargetProperty(animY, new PropertyPath("RenderTransform.ScaleY"));

        sb.Children.Add(animX);
        sb.Children.Add(animY);
        return sb;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _currentGraphAnimation?.Stop(GraphBorder);
        _currentButtonAnimation?.Stop();
        base.OnClosed(e);
    }
}