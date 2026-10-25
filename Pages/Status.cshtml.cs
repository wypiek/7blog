using Microsoft.AspNetCore.Mvc.RazorPages;
namespace SevenBlog.Pages;
public sealed class StatusModel : PageModel
{
    public int Code { get; private set; }
    public void OnGet(int code)
    {
        Code = code is >= 400 and <= 599 ? code : 404;
        Response.StatusCode = Code;
    }
}
