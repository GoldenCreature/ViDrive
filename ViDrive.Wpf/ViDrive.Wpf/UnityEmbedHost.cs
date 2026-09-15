using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace ViDrive.Wpf
{
    /// <summary>
    /// Win32 SetParent + Unity의 -parentHWND 커맨드라인 인자를 이용해
    /// 별도 프로세스로 실행된 Unity 창을 WPF 내부에 임베딩하는 HwndHost 구현체.
    /// WPF가 제공하는 hwndParent에 Unity를 직접 SetParent하지 않고,
    /// 그 사이에 순수 Win32 STATIC 창(완충 컨테이너)을 하나 더 두어
    /// WPF 내부 창 처리와의 충돌 가능성을 줄인다.
    /// (PoC 1단계: WPF-Unity 창 통합)
    /// </summary>
    public class UnityEmbedHost : HwndHost
    {
        private readonly string _unityExePath;
        private Process? _unityProcess;
        private IntPtr _unityHwnd = IntPtr.Zero;
        private IntPtr _containerHwnd = IntPtr.Zero;
        private IntPtr _jobHandle = IntPtr.Zero;

        public UnityEmbedHost(string unityExePath)
        {
            _unityExePath = unityExePath;
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            if (!File.Exists(_unityExePath))
            {
                throw new FileNotFoundException(
                    $"Unity 빌드 exe를 찾을 수 없습니다: {_unityExePath}");
            }

            // 완충 컨테이너를 만들기 전에, WPF가 지금 할당한 실제 영역 크기를 먼저 구해서
            // Unity 실행 인자에 넘겨준다. 이렇게 하면 Unity가 첫 프레임(스플래시 포함)부터
            // 이미 맞는 해상도로 시작해서, 스크립트가 리사이즈를 따라잡을 때까지의 어긋남이 줄어든다.
            NativeMethods.GetClientRect(hwndParent.Handle, out NativeMethods.RECT initialRect);
            int initialWidth = Math.Max(1, initialRect.Right - initialRect.Left);
            int initialHeight = Math.Max(1, initialRect.Bottom - initialRect.Top);

            // WPF의 hwndParent와 Unity 사이에 완충 역할을 할 순수 Win32 STATIC 창을 만든다.
            _containerHwnd = NativeMethods.CreateWindowEx(
                0, "static", "",
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
                0, 0, initialWidth, initialHeight,
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_containerHwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("완충 컨테이너 창 생성에 실패했습니다.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = _unityExePath,
                // delayed 옵션: Unity가 창을 만든 뒤 숨겨진 상태로 대기 → 우리가 SetParent 호출할 때까지 기다림
                // 부모로 WPF의 hwndParent가 아니라 방금 만든 완충 컨테이너 창을 지정한다.
                // -screen-width/-screen-height로 처음부터 맞는 해상도로 시작하게 한다.
                Arguments = $"-parentHWND {_containerHwnd.ToInt64()} delayed -screen-width {initialWidth} -screen-height {initialHeight}",
                UseShellExecute = false
            };

            _unityProcess = Process.Start(psi);

            // Job Object에 Unity 프로세스를 연결해둔다.
            // 이렇게 해두면 우리 WPF 프로세스가 정상 종료든, VS 디버거의 강제 중단이든,
            // 크래시든, 어떤 방식으로 죽어도 OS가 Job에 묶인 Unity 프로세스를 자동으로 같이 종료해준다.
            // (Window.Closing 이벤트가 아예 발생하지 않는 강제 종료 상황을 대비한 안전망)
            if (_unityProcess != null)
            {
                _jobHandle = NativeMethods.CreateJobObjectForKillOnClose();
                if (_jobHandle != IntPtr.Zero)
                {
                    NativeMethods.AssignProcessToJobObject(_jobHandle, _unityProcess.Handle);
                }
            }

            try
            {
                // Unity 창은 delayed 옵션 때문에 숨겨진 상태로 생성되므로,
                // 보이는 창만 찾는 MainWindowHandle 대신 EnumWindows로 프로세스 ID 기준 탐색한다.
                // 첫 실행은 셰이더 컴파일 등으로 느릴 수 있어 최대 약 30초까지 대기.
                IntPtr childHwnd = IntPtr.Zero;
                for (int i = 0; i < 200; i++)
                {
                    if (_unityProcess == null) break;

                    _unityProcess.Refresh();
                    if (_unityProcess.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"Unity 프로세스가 창을 띄우기 전에 종료되었습니다 (종료 코드: {_unityProcess.ExitCode}).");
                    }

                    childHwnd = NativeMethods.FindWindowByProcessId(_unityProcess.Id);
                    if (childHwnd != IntPtr.Zero) break;
                    Thread.Sleep(150);
                }

                if (childHwnd == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Unity 창 핸들을 가져오지 못했습니다 (타임아웃).");
                }

                _unityHwnd = childHwnd;

                // 1) Win32 부모-자식 관계를 완충 컨테이너 쪽으로 재설정
                NativeMethods.SetParent(_unityHwnd, _containerHwnd);

                // 2) 창 스타일을 자식 창(WS_CHILD)으로 변경 — 팝업/캡션/테두리 제거, 보이도록 설정
                int style = NativeMethods.GetWindowLong(_unityHwnd, NativeMethods.GWL_STYLE);
                style &= ~NativeMethods.WS_POPUP;
                style &= ~NativeMethods.WS_CAPTION;
                style &= ~NativeMethods.WS_THICKFRAME;
                style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
                NativeMethods.SetWindowLong(_unityHwnd, NativeMethods.GWL_STYLE, style);

                NativeMethods.ShowWindow(_unityHwnd, NativeMethods.SW_SHOW);
                NativeMethods.SendMessage(_unityHwnd, NativeMethods.WM_ACTIVATE, (IntPtr)NativeMethods.WA_ACTIVE, IntPtr.Zero);

                // HwndHost에는 완충 컨테이너의 핸들을 돌려준다 (Unity 창을 직접 돌려주지 않음).
                return new HandleRef(this, _containerHwnd);
            }
            catch
            {
                // 임베딩 도중 어디서든 실패하면, 이미 실행해버린 Unity 프로세스가
                // 좀비로 남지 않도록 여기서 확실히 정리하고 원래 예외를 그대로 던진다.
                Shutdown();
                throw;
            }
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            Shutdown();

            if (_containerHwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_containerHwnd);
                _containerHwnd = IntPtr.Zero;
            }
        }

        /// <summary>
        /// 완충 컨테이너와 Unity 창을 동시에 새 크기로 맞추고, 곧바로 활성화 메시지를 보내
        /// Unity가 새 크기로 렌더 타겟(백버퍼)을 재할당하도록 유도한다.
        /// (WBS "임베딩 Resize 처리" 항목에 해당)
        /// </summary>
        private void ResizeUnityWindow(int width, int height)
        {
            if (_containerHwnd != IntPtr.Zero)
            {
                NativeMethods.MoveWindow(_containerHwnd, 0, 0, width, height, true);
            }

            if (_unityHwnd != IntPtr.Zero)
            {
                NativeMethods.MoveWindow(_unityHwnd, 0, 0, width, height, true);
                NativeMethods.SendMessage(_unityHwnd, NativeMethods.WM_ACTIVATE, (IntPtr)NativeMethods.WA_ACTIVE, IntPtr.Zero);
            }
        }

        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            // 기본 구현이 _containerHwnd(this.Handle) 자체를 WPF 레이아웃 크기에 맞게
            // 리사이즈해준다. 이 호출이 빠지면 컨테이너 창 크기가 절대 안 바뀐다.
            base.OnWindowPositionChanged(rcBoundingBox);

            if (_containerHwnd != IntPtr.Zero)
            {
                NativeMethods.GetClientRect(_containerHwnd, out NativeMethods.RECT rect);
                ResizeUnityWindow(rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
        }

        /// <summary>
        /// 호스트 컨테이너 창에 도착하는 Win32 메시지를 직접 가로챈다.
        /// WM_SIZE는 드래그 리사이즈뿐 아니라 최대화/복원 등 모든 크기 변경 상황에서
        /// 예외 없이 발생하므로, OnWindowPositionChanged보다 더 확실한 트리거로 사용한다.
        /// </summary>
        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SIZE = 0x0005;

            if (msg == WM_SIZE)
            {
                long l = lParam.ToInt64();
                int width = (int)(l & 0xFFFF);
                int height = (int)((l >> 16) & 0xFFFF);
                ResizeUnityWindow(width, height);
            }

            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        /// <summary>
        /// WPF 창 종료 시 Unity 프로세스도 확실히 같이 종료한다 (좀비 프로세스 방지).
        /// </summary>
        public void Shutdown()
        {
            try
            {
                if (_unityProcess != null && !_unityProcess.HasExited)
                {
                    _unityProcess.Kill();
                    _unityProcess.WaitForExit(2000);
                }
            }
            catch
            {
                // 이미 종료된 경우 등은 무시
            }
            finally
            {
                _unityProcess?.Dispose();
                _unityProcess = null;
                _unityHwnd = IntPtr.Zero;

                if (_jobHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                }
            }
        }
    }

    internal static class NativeMethods
    {
        public const int GWL_STYLE = -16;
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_THICKFRAME = 0x00040000;
        public const int SW_SHOW = 5;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public const int WM_ACTIVATE = 0x0006;
        public const int WA_ACTIVE = 1;

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // ── Job Object: 부모(WPF) 프로세스가 어떤 방식으로 죽든 자식(Unity)도 같이 죽게 만드는 안전망 ──

        private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public long Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// "이 Job에 묶인 프로세스는, Job 핸들이 닫히는 순간 전부 강제 종료한다"는
        /// 옵션이 켜진 Job Object를 새로 만들어 반환한다.
        /// 우리 프로세스가 어떤 식으로든(정상 종료/강제 종료/크래시) 끝나면
        /// OS가 이 핸들을 정리하면서 자동으로 Unity 프로세스도 같이 죽인다.
        /// </summary>
        public static IntPtr CreateJobObjectForKillOnClose()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr infoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, infoPtr, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            return job;
        }

        /// <summary>
        /// 창이 숨겨져 있어도(비가시 상태여도) 특정 프로세스가 소유한 최상위 창을 찾는다.
        /// Process.MainWindowHandle은 "보이는 창"만 찾기 때문에 -parentHWND delayed 상황에서는 사용 불가.
        /// </summary>
        public static IntPtr FindWindowByProcessId(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == (uint)processId)
                {
                    found = hWnd;
                    return false; // 찾았으니 순회 중단
                }
                return true; // 계속 순회
            }, IntPtr.Zero);
            return found;
        }
    }
}