namespace Pdf.Native;

/// <summary>PDF 권한 비트. 암호화되지 않은 문서는 Raw = 0xFFFFFFFF.</summary>
public readonly record struct PdfPermissions(uint Raw, int SecurityRevision)
{
    public bool CanPrint => (Raw & 0x4) != 0;
    public bool CanModify => (Raw & 0x8) != 0;
    public bool CanCopy => (Raw & 0x10) != 0;
    public bool CanAnnotate => (Raw & 0x20) != 0;
    public bool CanAssemble => (Raw & 0x400) != 0 || CanModify;
    public bool CanEditPages => CanAssemble;
}
