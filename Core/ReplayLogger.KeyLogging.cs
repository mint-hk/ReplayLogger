using System;
using System.Collections.Generic;
using GlobalEnums;
using On;
using UnityEngine;

namespace ReplayLogger
{
    public partial class ReplayLogger
    {
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

            DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.Now.ToUnixTimeMilliseconds() - startUnixTime);
            customCanvas?.UpdateTime(dateTimeOffset.ToString("HH:mm:ss"));

            foreach (KeyCode keyCode in Enum.GetValues(typeof(KeyCode)))
            {
                if (Input.GetKeyDown(keyCode) || Input.GetKeyUp(keyCode))
                {
                    string keyStatus = Input.GetKeyDown(keyCode) ? "+" : "-";

                    long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();

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

            FlushKeyLogBufferIfNeeded(DateTimeOffset.Now.ToUnixTimeMilliseconds());
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
