using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// 임베딩된 Unity 창이 외부(WPF)에서 SetParent/MoveWindow로 리사이즈되었을 때,
/// Unity 엔진이 스스로 내부 렌더링 해상도(Screen.width/height)를 갱신하도록 만드는 스크립트.
///
/// 주의: Screen.SetResolution()은 내부 렌더 해상도뿐 아니라 창 자체의 위치/크기도
/// 다시 잡으려고 시도한다. 이걸 매 프레임 반복 호출하면 외부(WPF)의 MoveWindow와
/// 서로 충돌해서 드래그 중 창이 떨리거나 끊기는 현상이 생긴다.
/// 그래서 크기가 일정 프레임 동안 "변화 없이 안정된 상태"일 때만 실제로 반영한다
/// (디바운싱) — 드래그 도중엔 아무 것도 하지 않고, 마우스를 멈춘 뒤에야 한 번 적용된다.
///
/// 사용법: 씬 안의 아무 활성 GameObject(예: Main Camera)에 컴포넌트로 붙이면 된다.
/// </summary>
public class AutoResizeToWindow : MonoBehaviour
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // 크기가 이만큼의 프레임 동안 연속으로 "변화 없음" 상태여야 실제로 적용한다.
    // 값이 너무 작으면 드래그 중에도 반응해서 떨림이 남고, 너무 크면 반응이 굼떠 보인다.
    private const int STABLE_FRAMES_REQUIRED = 15;

    private int _lastAppliedWidth = -1;
    private int _lastAppliedHeight = -1;

    private int _pendingWidth = -1;
    private int _pendingHeight = -1;
    private int _stableFrameCount;

    // 시작 직후 첫 적용은 디바운싱 없이 즉시 반영한다.
    // (그 이후 사용자가 드래그로 리사이즈할 때만 디바운싱을 적용해 떨림을 막는다)
    private bool _firstApplyDone;

    void Update()
    {
        IntPtr hwnd = GetActiveWindow();
        if (hwnd == IntPtr.Zero) return;

        if (!GetClientRect(hwnd, out RECT rect)) return;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        if (!_firstApplyDone)
        {
            Screen.SetResolution(width, height, false);
            _lastAppliedWidth = width;
            _lastAppliedHeight = height;
            _pendingWidth = width;
            _pendingHeight = height;
            _stableFrameCount = STABLE_FRAMES_REQUIRED;
            _firstApplyDone = true;
            return;
        }

        if (width == _pendingWidth && height == _pendingHeight)
        {
            _stableFrameCount++;
        }
        else
        {
            _pendingWidth = width;
            _pendingHeight = height;
            _stableFrameCount = 0;
        }

        bool justBecameStable = _stableFrameCount == STABLE_FRAMES_REQUIRED;
        bool sizeActuallyChanged = width != _lastAppliedWidth || height != _lastAppliedHeight;

        if (justBecameStable && sizeActuallyChanged)
        {
            Screen.SetResolution(width, height, false);
            _lastAppliedWidth = width;
            _lastAppliedHeight = height;
        }
    }
}