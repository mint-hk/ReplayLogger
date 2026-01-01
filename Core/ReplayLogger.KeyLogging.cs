using System;
using System.Collections.Generic;
using GlobalEnums;
using On;
using UnityEngine;

namespace ReplayLogger
{
    public partial class ReplayLogger
    {
        private static readonly KeyCode[] CachedKeyCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode));
        private static readonly KeyCode[] RelevantKeyCodes = GenerateRelevantKeyCodes();

        private static KeyCode[] GenerateRelevantKeyCodes()
        {
            var list = new System.Collections.Generic.List<KeyCode>();
            for (int i = (int)KeyCode.A; i <= (int)KeyCode.Z; i++) list.Add((KeyCode)i);
            for (int i = (int)KeyCode.Alpha0; i <= (int)KeyCode.Alpha9; i++) list.Add((KeyCode)i);
            for (int i = (int)KeyCode.Keypad0; i <= (int)KeyCode.Keypad9; i++) list.Add((KeyCode)i);
            for (int i = (int)KeyCode.F1; i <= (int)KeyCode.F15; i++) list.Add((KeyCode)i);
            for (int i = (int)KeyCode.JoystickButton0; i <= (int)KeyCode.JoystickButton19; i++) list.Add((KeyCode)i);

            list.AddRange(new[] {
                KeyCode.Space, KeyCode.Return, KeyCode.Escape, KeyCode.Backspace, KeyCode.Tab,
                KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
                KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftArrow, KeyCode.RightArrow,
                KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2
            });
            return list.ToArray();
        }

        private void CheckPressedKey(On.GameManager.orig_Update orig, GameManager self)
        {
            orig(self);
            if (!isPlayChalange)
            {
                return;
            }

            MonitorDebugModUi();
            if (GameManager.instance.gameState == GlobalEnums.GameState.CUTSCENE && lastScene == "GG_Radiance")
            {
                Close();
            }

            string roomName = GameManager.instance?.sceneName ?? lastScene;
            speedWarnTracker.Update(writer, roomName, lastUnixTime);
            hitWarnTracker.Update(writer, roomName, lastUnixTime);
            FlushWarningsIfNeeded(speedWarnBuffer, speedWarnTracker.Warnings, speedWarnTracker.ClearWarnings);
            FlushWarningsIfNeeded(hitWarnBuffer, hitWarnTracker.Warnings, hitWarnTracker.ClearWarnings);

            long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(unixTime - startUnixTime);
            customCanvas?.UpdateTime(dateTimeOffset.ToString("HH:mm:ss"));

            // OPTIMIZED: Early exit if no input detected - saves ~21,000 checks per second at 60fps
            if (!Input.anyKeyDown && !Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1) && !Input.GetMouseButtonDown(2))
            {
                FlushKeyLogBufferIfNeeded(unixTime);
                return;
            }

            // Only check relevant keys (~100 instead of 350)
            foreach (KeyCode keyCode in RelevantKeyCodes)
            {
                if (Input.GetKeyDown(keyCode) || Input.GetKeyUp(keyCode))
                {
                    string keyStatus = Input.GetKeyDown(keyCode) ? "+" : "-";

                    float fps = Time.unscaledDeltaTime == 0 ? lastFps : 1f / Time.unscaledDeltaTime;
                    lastFps = fps;
                    customCanvas?.UpdateWatermark(keyCode);
                    int watermarkNumber = customCanvas?.numberInCanvas?.Number ?? 0;
                    Color watermarkColorStruct = (customCanvas != null && customCanvas.numberInCanvas != null)
                        ? customCanvas.numberInCanvas.Color
                        : Color.white;
                    string watermarkColor = ColorUtility.ToHtmlStringRGBA(watermarkColorStruct);
                    string formattedKey = JoystickKeyMapper.FormatKey(keyCode);
                    string logEntry = $"+{unixTime - lastUnixTime}|{formattedKey}|{keyStatus}|{watermarkNumber}|#{watermarkColor}|{fps.ToString("F0")}|";
                    try
                    {
                        keyLogBuffer.Add(logEntry);
                    }
                    catch (Exception e)
                    {
                        Modding.Logger.LogError("Key log write failed: " + e.Message);
                    }

                    if (keyStatus == "+")
                    {
                        string arenaForHotkey = lastScene;
                        debugHotkeysTracker.TrackActivation(keyCode, arenaForHotkey, lastUnixTime, unixTime);
                    }
                }
            }

            FlushKeyLogBufferIfNeeded(unixTime);
        }

        private void FlushWarningsIfNeeded(BufferedLogSection buffer, IReadOnlyList<string> warnings, Action clearAction)
        {
            if (buffer == null || warnings == null)
            {
                return;
            }

            if (warnings.Count < BufferedSectionThreshold)
            {
                return;
            }

            buffer.AddRange(warnings);
            clearAction?.Invoke();
        }

        private void FlushKeyLogBufferIfNeeded(long now, bool force = false)
        {
            if (writer == null || keyLogBuffer.Count == 0)
            {
                return;
            }

            if (!force)
            {
                if (now - lastKeyLogFlushTime < KeyLogFlushIntervalMs && keyLogBuffer.Count < KeyLogFlushBatchSize)
                {
                    return;
                }
            }

            foreach (string entry in keyLogBuffer)
            {
                LogWrite.EncryptedLine(writer, entry);
            }

            keyLogBuffer.Clear();
            lastKeyLogFlushTime = now;
        }
    }
}
