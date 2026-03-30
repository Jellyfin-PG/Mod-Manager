using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ModManager.Controllers
{
    /// <summary>
    /// Catches all requests to /ModManager/mods/{modId}/api/{**path} and
    /// dispatches them to whichever JsRuntime registered a matching route.
    /// No authentication is enforced here — individual mods can inspect
    /// req.headers["Authorization"] themselves if they need it.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("ModManager/mods/{modId}/api/{**path}")]
    public class ModManagerController : ControllerBase
    {
        [HttpGet]
        [HttpPost]
        [HttpPut]
        [HttpDelete]
        [HttpPatch]
        public async Task<IActionResult> Handle(string modId, string path)
        {
            var loader = Plugin.Instance?.ModLoader;
            if (loader == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            var handled = await loader.TryHandleRequestAsync(HttpContext);
            if (!handled)
                return NotFound(new { error = "No route matched", modId, path });

            return new EmptyResult();
        }
    }
}
