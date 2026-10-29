namespace StudioX.Ai8051Isp;

/// <summary>CLI 与 IDE 共用配置写入和失败处理；选项只能跟在完整固件 ACK 后面。</summary>
public static class IspSession
{
    public static async Task<IspSessionResult> ExecuteAsync(BridgeProtocol bridge, FirmwareImage? firmware,
        DeviceConfiguration? configuration, Action<object> progress, CancellationToken token, bool recoverErased = false)
    {
        configuration?.Validate();
        if (configuration is not null && firmware is null)
        {
            throw new InvalidOperationException("Configuration requires firmware.");
        }
        bool erased = false, optionsAttempted = false;
        try
        {
            var target = await bridge.Detect(token);
            if (firmware is not null)
            {
                DeviceConfiguration.CheckExisting(target, recoverErased);
            }
            Calibration? calibration = configuration is null ? null : await bridge.Calibrate(configuration, token);
            if (firmware is not null)
            {
                await bridge.BeginProgramming(calibration, token);
                erased = true;
                await bridge.Program(firmware.Code, token);
            }
            if (calibration is not null)
            {
                optionsAttempted = true;
                await bridge.WriteConfiguration(calibration, token);
            }
            await bridge.Run(token);
            if (calibration is not null)
            {
                await Task.Delay(300, token);
                target = await bridge.Detect(token);
                DeviceConfiguration.CheckExisting(target);
                byte[] status = Convert.FromHexString(target.RawStatus);
                if (target.IrcHz != calibration.MeasuredHz || status[5] != calibration.Trim ||
                    status[6] != calibration.Range || status[7] != calibration.Divider)
                {
                    throw new InvalidDataException("Configuration readback differs from written calibration.");
                }
                await bridge.Run(token);
            }
            return new(target, firmware is not null, calibration is not null);
        }
        catch (Exception error)
        {
            progress(new
            {
                stage = "failed",
                message = error.ToString(),
                imageMayBeIncomplete = erased,
                configurationMayBeIncomplete = optionsAttempted
            });
            // 不自动重发擦写命令；仅在擦写之前失败才尝试让原程序恢复。
            if (!erased && !optionsAttempted)
            {
                try
                {
                    await bridge.Run(CancellationToken.None);
                }
                catch (Exception cleanup) { progress(new { stage = "recovery-failed", message = cleanup.ToString() }); }
            }
            throw;
        }
    }
}
