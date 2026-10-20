using System.Runtime.InteropServices;

namespace ParkingGuidance.LedBridge;

/// <summary>
/// Khai báo P/Invoke cho HDSDK.dll — chép đúng từ CSDKExport.cs của dự án PGS (đã đối chiếu số tham số với lệnh "ret N" trong DLL).
/// Chuỗi truyền dạng con trỏ Unicode (Marshal.StringToHGlobalUni) như PGS; NULL = IntPtr.Zero.
/// SDK giữ 1 màn hình toàn cục: Hd_CreateScreen → Hd_AddProgram → Hd_AddArea → Hd_AddSimpleTextAreaItem → Hd_SendScreen.
/// </summary>
internal static class HdSdk
{
    private const string Dll = "HDSDK.dll";

    [DllImport(Dll, EntryPoint = "Hd_GetSDKLastError", CallingConvention = CallingConvention.StdCall)]
    public static extern int Hd_GetSDKLastError();

    [DllImport(Dll, EntryPoint = "Hd_CreateScreen", CallingConvention = CallingConvention.StdCall)]
    public static extern int Hd_CreateScreen(int nWidth, int nHeight, int nColor, int nGray, int nCardType, IntPtr pExParamsBuf, int nBufSize);

    [DllImport(Dll, EntryPoint = "Hd_AddProgram", CallingConvention = CallingConvention.StdCall)]
    public static extern int Hd_AddProgram(IntPtr pBoderImgPath, int nBorderEffect, int nBorderSpeed, IntPtr pExParamsBuf, int nBufSize);

    [DllImport(Dll, EntryPoint = "Hd_AddArea", CallingConvention = CallingConvention.StdCall)]
    public static extern int Hd_AddArea(int nProgramID, int nX, int nY, int nWidth, int nHeight, IntPtr pBoderImgPath, int nBorderEffect, int nBorderSpeed, IntPtr pExParamsBuf, int nBufSize);

    [DllImport(Dll, EntryPoint = "Hd_AddSimpleTextAreaItem", CallingConvention = CallingConvention.StdCall)]
    public static extern int Hd_AddSimpleTextAreaItem(int nAreaID, IntPtr pText, int nTextColor, int nBackGroupColor, int nStyle, IntPtr pFontName, int nFontHeight, int nShowEffect, int nShowSpeed, int nClearType, int nStayTime, IntPtr pExParamsBuf, int nBufSize);

    [DllImport(Dll, EntryPoint = "Hd_AddImageAreaItem", CallingConvention = CallingConvention.StdCall)]
    public static extern int Hd_AddImageAreaItem(int nAreaID, IntPtr pPaths, int nShowEffect, int nShowSpeed, int nClearType, int nStayTime, IntPtr pExParamsBuf, int nBufSize);

    [DllImport(Dll, EntryPoint = "Hd_SendScreen", CallingConvention = CallingConvention.StdCall)]
    public static extern int Hd_SendScreen(int nSendType, IntPtr pStrParams, IntPtr pDeviceGUID, IntPtr pExParamsBuf, int nBufSize);

    [DllImport(Dll, EntryPoint = "Hd_GetColor", CallingConvention = CallingConvention.StdCall)]
    public static extern int Hd_GetColor(int r, int g, int b);

    [DllImport(Dll, EntryPoint = "Cmd_IsCardOnline", CallingConvention = CallingConvention.StdCall)]
    public static extern int Cmd_IsCardOnline(int nSendType, IntPtr pStrParams, IntPtr pDeviceGUID);
}
