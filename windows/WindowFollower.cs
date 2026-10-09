using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexQuotaBar
{
    // WinEvent notifications run on the registering UI thread. Translation only
    // needs cached relative geometry, never a synchronous accessibility query.
    sealed class WindowFollower : IDisposable
    {
        delegate void WinEventCallback(IntPtr hook,uint eventType,IntPtr window,int objectId,int childId,uint thread,uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEventCallback callback,uint process,uint thread,uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        readonly Control overlay;
        readonly Func<bool> canFollow;
        readonly Action geometryChanged;
        readonly WinEventCallback callback;
        readonly Timer movingTimer = new Timer { Interval=16 };
        IntPtr target, locationHook, moveHook;
        Rectangle ownerBounds, overlayBounds;
        int dpi;
        bool positioned, disposed;
        public bool EventsAvailable { get { return locationHook!=IntPtr.Zero; } }
        public bool Moving { get { return movingTimer.Enabled; } }
        public WindowFollower(Control overlay,Func<bool> canFollow,Action geometryChanged)
        {
            this.overlay=overlay; this.canFollow=canFollow; this.geometryChanged=geometryChanged;
            callback=OnWindowEvent;
            movingTimer.Tick += delegate { FollowNow(); };
        }
        public void Attach(IntPtr window)
        {
            Detach(); if(disposed || window==IntPtr.Zero) return;
            target=window; uint process; Native.GetWindowThreadProcessId(window,out process);
            if(process==0) { target=IntPtr.Zero; return; }
            // EVENT_OBJECT_LOCATIONCHANGE, and EVENT_SYSTEM_MOVESIZESTART/END.
            // Scope to the target process, then filter its exact top-level handle.
            locationHook=SetWinEventHook(0x800B,0x800B,IntPtr.Zero,callback,process,0,0);
            moveHook=SetWinEventHook(0x000A,0x000B,IntPtr.Zero,callback,process,0,0);
        }
        public void RememberPlacement(Rectangle owner,int ownerDpi,Rectangle bounds)
        {
            if(disposed || target==IntPtr.Zero) return;
            ownerBounds=owner; overlayBounds=bounds; dpi=ownerDpi; positioned=true;
        }
        void OnWindowEvent(IntPtr hook,uint eventType,IntPtr window,int objectId,int childId,uint thread,uint time)
        {
            if(disposed || window!=target || objectId!=0 || childId!=0) return;
            try {
                if(eventType==0x000A) { if(canFollow()) movingTimer.Start(); FollowNow(); }
                else if(eventType==0x000B) { movingTimer.Stop(); FollowNow(); geometryChanged(); }
                else if(eventType==0x800B) FollowNow();
            } catch { /* Never propagate managed exceptions across a native callback. */ }
        }
        void FollowNow()
        {
            if(disposed || !positioned || !overlay.IsHandleCreated || !canFollow()) return;
            Native.Rect native;
            if(!Native.GetWindowRect(target,out native)) { positioned=false; movingTimer.Stop(); return; }
            var next=native.ToRectangle();
            if(next.Size!=ownerBounds.Size || Native.Dpi(target)!=dpi) {
                positioned=false; geometryChanged(); return;
            }
            if(next.Location==ownerBounds.Location) return;
            var translated=overlayBounds;
            translated.Offset(next.Left-ownerBounds.Left,next.Top-ownerBounds.Top);
            // Preserve candidate-window order and focus. Only change physical x/y.
            if(Native.SetWindowPos(overlay.Handle,IntPtr.Zero,translated.X,translated.Y,0,0,0x0010|0x0004|0x0001|0x0200)) {
                overlayBounds=translated; ownerBounds=next;
            }
        }
        void Detach()
        {
            movingTimer.Stop(); positioned=false; target=IntPtr.Zero;
            if(locationHook!=IntPtr.Zero) { UnhookWinEvent(locationHook); locationHook=IntPtr.Zero; }
            if(moveHook!=IntPtr.Zero) { UnhookWinEvent(moveHook); moveHook=IntPtr.Zero; }
        }
        public void Dispose() { if(disposed) return; Detach(); disposed=true; movingTimer.Dispose(); }
    }
}
