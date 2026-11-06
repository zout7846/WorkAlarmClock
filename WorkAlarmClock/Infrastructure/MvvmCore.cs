using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace WorkAlarmClock.Infrastructure
{
    /// <summary>
    /// MVVM 基础设施：属性变更通知基类。
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        /// <summary>属性变更事件（UI 线程触发）。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>触发属性变更通知。</summary>
        /// <param name="propertyName">属性名，默认由编译器填充。</param>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 设置属性值并在值变化时触发通知。
        /// </summary>
        /// <typeparam name="T">属性类型。</typeparam>
        /// <param name="field">属性后备字段。</param>
        /// <param name="value">新值。</param>
        /// <param name="propertyName">属性名，默认由编译器填充。</param>
        /// <returns>值是否发生变化。</returns>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>批量触发若干属性的变更通知。</summary>
        /// <param name="propertyNames">属性名集合。</param>
        protected void RaisePropertiesChanged(params string[] propertyNames)
        {
            foreach (string name in propertyNames)
            {
                OnPropertyChanged(name);
            }
        }
    }

    /// <summary>
    /// 轻量命令实现（替代 CommunityToolkit 的 RelayCommand）。
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        /// <summary>命令可执行状态变化事件（WPF 会自动监听）。</summary>
        public event EventHandler? CanExecuteChanged;

        /// <summary>使用无参执行委托构造命令。</summary>
        /// <param name="execute">执行逻辑。</param>
        /// <param name="canExecute">可执行判定，null 表示始终可执行。</param>
        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = _ => execute();
            if (canExecute is not null)
            {
                _canExecute = _ => canExecute();
            }
        }

        /// <summary>使用带参执行委托构造命令。</summary>
        /// <param name="execute">执行逻辑。</param>
        /// <param name="canExecute">可执行判定，null 表示始终可执行。</param>
        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = execute;
            _canExecute = canExecute;
        }

        /// <summary>判定命令是否可执行。</summary>
        /// <param name="parameter">命令参数。</param>
        /// <returns>true 表示可执行。</returns>
        public bool CanExecute(object? parameter) => _canExecute is null || _canExecute(parameter);

        /// <summary>执行命令。</summary>
        /// <param name="parameter">命令参数。</param>
        public void Execute(object? parameter) => _execute(parameter);

        /// <summary>通知 WPF 重新查询可执行状态。</summary>
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

namespace WorkAlarmClock.Services
{
    /// <summary>
    /// 唯一时钟节拍（UI 线程，默认 1 秒），可注入时间源便于测试。
    /// 与 MVVM 基础设施同文件管理：二者同为无业务依赖的底层能力。
    /// </summary>
    public interface ITicker
    {
        /// <summary>当前时刻（默认本地时间）。</summary>
        DateTimeOffset Now { get; }

        /// <summary>节拍事件（UI 线程）。</summary>
        event EventHandler? Tick;

        /// <summary>启动节拍。</summary>
        void Start();

        /// <summary>停止节拍（供退出或测试使用）。</summary>
        void Stop();
    }

    /// <summary>
    /// 基于 <see cref="DispatcherTimer"/> 的时钟节拍实现，全应用唯一。
    /// </summary>
    public sealed class ClockTicker : ITicker, IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Func<DateTimeOffset> _clockSource;

        /// <summary>使用自定义时钟源与间隔构造节拍器。</summary>
        /// <param name="clockSource">时间源，默认 <see cref="DateTimeOffset.Now"/>。</param>
        /// <param name="interval">节拍间隔，默认 1 秒。</param>
        public ClockTicker(Func<DateTimeOffset>? clockSource = null, TimeSpan? interval = null)
        {
            _clockSource = clockSource ?? (() => DateTimeOffset.Now);
            _timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = interval ?? TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
        }

        /// <inheritdoc />
        public DateTimeOffset Now => _clockSource();

        /// <inheritdoc />
        public event EventHandler? Tick;

        /// <inheritdoc />
        public void Start()
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        /// <summary>停止节拍（供退出或测试使用）。</summary>
        public void Stop()
        {
            if (_timer.IsEnabled)
            {
                _timer.Stop();
            }
        }

        /// <summary>释放：停止节拍定时器。</summary>
        public void Dispose() => Stop();
    }
}
