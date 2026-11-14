using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ReactiveUI.Avalonia;
using ChameLab.Mobile;
using ChameLab.Services;
using ChameLab.Transport;

namespace ChameLab.Android
{
#if CHAMELAB_UI_TESTS
    // adb run-as installs the synthetic request and retrieves test results. This flag
    // grants that access only to the isolated test package; Release/AOT stays enabled.
    [Application(Debuggable = true)]
#else
    [Application]
#endif
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {

#if CHAMELAB_UI_TESTS
            AppServices.Register([], store: new MemoryKeyValueStore());
#else
            AppServices.Register(new ITransportScanner[] { new UsbCdcScanner(new AndroidUsbCdcHost()), new BleScanner() }, dfu: new IDfuLocator[] { new BleDfuLocator() },
                feedback: new AndroidFeedbackPlayer(), hardnested: new ManagedHardnestedEngine(), wakeLock: new WakeLockService(AndroidWakeLock.AcquireAsync));
#endif
            return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI(_ => { });
        }
    }
}
