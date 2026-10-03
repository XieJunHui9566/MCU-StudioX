using StudioX.Application;

/// <summary>扫描标记仅用于发现诊断；真实 UI 构建复用共享 LVGL，不复制库或开发环境组件。</summary>
static class CustomUiFixtures
{
    public static async Task WriteLibraryMarkersAsync(string root, int major = 8, int minor = 3,
        int patch = 11, bool includeCore = true)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "lvgl.h"),
            $"#define LVGL_VERSION_MAJOR {major}\n#define LVGL_VERSION_MINOR {minor}\n#define LVGL_VERSION_PATCH {patch}\n");
        foreach (var relative in includeCore
            ? new[] { "src/core/lv_obj.c", "src/core/lv_obj.h", "src/hal/lv_hal_disp.c", "src/misc/lv_mem.c", "src/lv_conf_internal.h", "src/extra/lv_extra.c" }
            : new[] { "src/hal/lv_hal_disp.c", "src/misc/lv_mem.c", "src/lv_conf_internal.h", "src/extra/lv_extra.c" })
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "/* Scan-only fixture: this is not a compilable LVGL distribution. */\n");
        }
    }

    public static async Task WriteActualUiAsync(WorkbenchService services, string sourceProject, string fixture)
    {
        Directory.CreateDirectory(Path.Combine(fixture, ".studiox"));
        Directory.CreateDirectory(Path.Combine(fixture, "src", "pages"));
        Directory.CreateDirectory(Path.Combine(fixture, "include"));
        Directory.CreateDirectory(Path.Combine(fixture, "assets", "images"));
        Directory.CreateDirectory(Path.Combine(fixture, "assets", "runtime"));
        File.Copy(Path.Combine(sourceProject, ".studiox", "project.json"), Path.Combine(fixture, ".studiox", "project.json"), true);
        CopyDeviceMetadata(sourceProject, fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture, "assets", "runtime", "banner.bin"), "custom-resource-ok");
        await File.WriteAllTextAsync(Path.Combine(fixture, "include", "lv_conf_pc.h"), """
            #ifndef LV_CONF_H
            #define LV_CONF_H
            #define LV_COLOR_DEPTH 16
            #define LV_MEM_SIZE (36U * 1024U)
            #define LV_TICK_CUSTOM 1
            #define LV_TICK_CUSTOM_INCLUDE "lv_port_clock.h"
            #define LV_TICK_CUSTOM_SYS_TIME_EXPR (lv_port_millis())
            #define LV_USE_LOG 1
            #define LV_LOG_LEVEL LV_LOG_LEVEL_WARN
            #define LV_LOG_PRINTF 0
            #define LV_ASSERT_HANDLER_INCLUDE "lv_port.h"
            #define LV_ASSERT_HANDLER lv_port_panic();
            #define LV_DISP_DEF_REFR_PERIOD 5
            #define LV_INDEV_DEF_READ_PERIOD 5
            #define LV_THEME_DEFAULT_TRANSITION_TIME 0
            #endif
            """);
        await File.WriteAllTextAsync(Path.Combine(fixture, "include", "custom_ui.h"), """
            #pragma once
            #include "lvgl.h"
            #ifdef __cplusplus
            extern "C" {
            #endif
            void custom_ui_start(void);
            void custom_details_create(lv_obj_t *screen);
            extern const lv_img_dsc_t custom_icon;
            #ifdef __cplusplus
            }
            #endif
            """);
        await File.WriteAllTextAsync(Path.Combine(fixture, "src", "dashboard.c"), """
            #include "custom_ui.h"
            #include <stdio.h>
            #include <string.h>
            static void details_clicked(lv_event_t *event)
            {
                (void)event;
                lv_obj_t *screen = lv_obj_create(NULL);
                custom_details_create(screen);
                lv_scr_load(screen);
            }
            void custom_ui_start(void)
            {
                char resource[32] = {0};
                FILE *file = fopen("assets/runtime/banner.bin", "rb");
                if (file) { (void)fread(resource, 1, sizeof(resource) - 1, file); fclose(file); }
                lv_obj_set_style_bg_color(lv_scr_act(), strcmp(resource, "custom-resource-ok") == 0
                    ? lv_color_make(20, 40, 60) : strcmp(resource, "custom-resource-new") == 0
                    ? lv_color_make(80, 100, 120) : lv_color_make(255, 128, 0), 0);
                lv_obj_set_style_bg_opa(lv_scr_act(), LV_OPA_COVER, 0);
                lv_obj_t *image = lv_img_create(lv_scr_act());
                lv_img_set_src(image, &custom_icon);
                lv_obj_set_pos(image, 100, 5);
                lv_obj_t *button = lv_btn_create(lv_scr_act());
                lv_obj_set_pos(button, 10, 10);
                lv_obj_set_size(button, 70, 30);
                lv_obj_add_event_cb(button, details_clicked, LV_EVENT_CLICKED, NULL);
                lv_obj_t *label = lv_label_create(button);
                lv_label_set_text(label, "Details");
                lv_obj_center(label);
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(fixture, "src", "pages", "details.cpp"), """
            #include "lvgl/lvgl.h"
            #include "custom_ui.h"
            #include <string>
            extern "C" void custom_details_create(lv_obj_t *screen)
            {
                std::string title = std::string("Custom ") + "details";
                lv_obj_set_style_bg_color(screen, lv_color_make(200, 20, 200), 0);
                lv_obj_set_style_bg_opa(screen, LV_OPA_COVER, 0);
                lv_obj_t *label = lv_label_create(screen);
                lv_label_set_text(label, title.c_str());
                lv_obj_set_pos(label, 5, 5);
            }
            """);
        var pixels = string.Join(",", Enumerable.Repeat("0xe0,0x07", 64));
        await File.WriteAllTextAsync(Path.Combine(fixture, "assets", "images", "custom_icon.c"), $$"""
            #include "custom_ui.h"
            static const uint8_t pixels[] = { {{pixels}} };
            const lv_img_dsc_t custom_icon = {
                .header = { .always_zero = 0, .w = 8, .h = 8, .cf = LV_IMG_CF_TRUE_COLOR },
                .data_size = sizeof(pixels), .data = pixels
            };
            """);
    }

    public static void CopyDeviceMetadata(string sourceProject, string fixture)
    {
        var manifest = Path.Combine(sourceProject, "device", "manifest.json");
        if (!File.Exists(manifest))
        {
            return;
        }
        Directory.CreateDirectory(Path.Combine(fixture, "device"));
        File.Copy(manifest, Path.Combine(fixture, "device", "manifest.json"), true);
    }
}
