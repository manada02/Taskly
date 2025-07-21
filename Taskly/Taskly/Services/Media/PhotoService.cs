using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Taskly.Services.Media
{
    public class PhotoService
    {
        // Pořízení nové fotky pomocí fotoaparátu daného zařízení
        public static async Task<byte[]?> CapturePhotoAsync()
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    using var stream = await photo.OpenReadAsync();
                    using var memoryStream = new MemoryStream();
                    await stream.CopyToAsync(memoryStream);
                    return memoryStream.ToArray();
                }
            }
            return null;
        }

        // Výběr existující fotky z galerie zařízení
        public static async Task<byte[]?> PickPhotoAsync()
        {
            var photo = await MediaPicker.Default.PickPhotoAsync();
            if (photo != null)
            {
                using var stream = await photo.OpenReadAsync();
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream);
                return memoryStream.ToArray();
            }
            return null;
        }
    }
}