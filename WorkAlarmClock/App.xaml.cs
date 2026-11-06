using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Services;
using WorkAlarmClock.ViewModels;

namespace WorkAlarmClock
{
    /// <summary>
    /// 应用入口：单实例、组合根初始化、桌宠窗口创建、全局异常兜底与退出清理。
    /// </summary>
    public partial class App : System.Windows.Application, IHotkeyActions
    {
        private static Mutex? _singleInstanceMutex;
        private static MainViewModel? _mainViewModel;
        private static MainWindow? _mainWindow;
        private static PetWindow? _petWindow;

        /// <summary>主面板视图模型（托盘与主面板共用同一实例）。</summary>
        internal static MainViewModel MainViewModel => _mainViewModel ??= new MainViewModel();

        /// <inheritdoc />
        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstanceMutex = new Mutex(true, AppEnvironment.SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "WorkAlarmClock 已经在运行了，请查看桌面右下角托盘。",
                    "WorkAlarmClock",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            try
            {
                AppEnvironment.EnsureDirectories();
                AppServices.Initialize();


                MainViewModel viewModel = MainViewModel;
            AppServices.AttachTray(new TrayMenuCallbacks(
                openMainPanel: ShowMainPanel,
                startWork: () => viewModel.StartWorkCommand.Execute(null),
                startRest: () => viewModel.StartRestCommand.Execute(null),
                endDay: () => viewModel.EndDayCommand.Execute(null),
                generateReport: () => viewModel.GenerateReportCommand.Execute(null),
                focusToggle: () => viewModel.FocusToggleCommand.Execute(null),
                openSettings: () => viewModel.OpenSettingsCommand.Execute(null),
                togglePet: () => viewModel.TogglePetCommand.Execute(null),
                screenshot: () => viewModel.ScreenshotCommand.Execute(null),
                exitApp: ExitApplication));

                _petWindow = new PetWindow();
                _petWindow.Show();

                // 安装全局热键（R-22）；订阅桌宠显隐切换事件（热键/菜单/命令共用一路）。
                AppServices.PetToggleRequested += TogglePetWindow;
                AppServices.Hotkey.Install(this);

                AppEnvironment.Log("桌宠窗口已显示。");
            }
            catch (Exception ex)
            {
                AppEnvironment.Log(ex);
                System.Windows.MessageBox.Show(
                    "启动失败：" + ex.Message,
                    "WorkAlarmClock",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
            }
        }

        /// <inheritdoc />
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                AppServices.Shutdown();
                AppEnvironment.Log("应用程序已退出。");
            }
            catch (Exception ex)
            {
                AppEnvironment.Log(ex);
            }
            finally
            {
                try
                {
                    _singleInstanceMutex?.ReleaseMutex();
                    _singleInstanceMutex?.Dispose();
                    _singleInstanceMutex = null;
                }
                catch (Exception ex)
                {
                    AppEnvironment.Log(ex);
                }

                base.OnExit(e);
            }
        }

        /// <summary>显示（或激活）主面板。</summary>
        internal static void ShowMainPanel()
        {
            try
            {
                if (_mainWindow is null)
                {
                    _mainWindow = new MainWindow { DataContext = MainViewModel };
                }

                if (!_mainWindow.IsVisible)
                {
                    _mainWindow.Show();
                }

                if (_mainWindow.WindowState == WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Normal;
                }

                _mainWindow.Activate();
                _mainWindow.Focus();
            }
            catch (Exception ex)
            {
                AppEnvironment.Log("打开主面板失败。", ex);
            }
        }

        /// <summary>退出应用（托盘菜单与主面板共用）。</summary>
        internal static void ExitApplication()
        {
            try
            {
                _petWindow?.Close();
                _mainWindow?.Close();
            }
            catch (Exception ex)
            {
                AppEnvironment.Log("关闭窗口失败。", ex);
            }

            AppServices.RequestExit();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppEnvironment.Log(e.Exception);
            System.Windows.MessageBox.Show(
                "出现未处理的异常，已记录日志：" + e.Exception.Message,
                "WorkAlarmClock",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
        }

        private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppEnvironment.Log(ex);
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppEnvironment.Log(e.Exception);
            e.SetObserved();
        }

        /// <summary>显示/隐藏桌宠窗口（热键与托盘菜单共用；静默切换，不抢焦点、不弹其它窗口）。</summary>
        private static void TogglePetWindow()
        {
            if (_petWindow is null)
            {
                return;
            }

            if (_petWindow.IsVisible)
            {
                _petWindow.Hide();
                AppEnvironment.Log("桌宠已隐藏。");
            }
            else
            {
                // ShowActivated=False + WS_EX_TOOLWINDOW：显示时不激活、不进 Alt+Tab，
                // 因此不会打断当前正在操作的窗口。
                _petWindow.Show();
                AppEnvironment.Log("桌宠已显示。");
            }
        }

        /// <inheritdoc />
        void IHotkeyActions.StartWork() => MainViewModel.StartWorkCommand.Execute(null);

        /// <inheritdoc />
        void IHotkeyActions.StartRest() => MainViewModel.StartRestCommand.Execute(null);

        /// <inheritdoc />
        void IHotkeyActions.EndDay() => MainViewModel.EndDayCommand.Execute(null);

        /// <inheritdoc />
        void IHotkeyActions.GenerateReport() => AppServices.GenerateReportNow();

        /// <inheritdoc />
        void IHotkeyActions.TogglePet() => AppServices.RaisePetToggle();

        /// <inheritdoc />
        void IHotkeyActions.Screenshot() => AppServices.Screenshot.StartCapture();

        /// <inheritdoc />
        void IHotkeyActions.FocusToggle()
        {
            if (AppServices.Focus.IsRunning)
            {
                AppServices.Focus.Stop();
            }
            else
            {
                AppServices.Focus.Start();
            }
        }
    }
}
