using System;
using System.Linq;

namespace LegionFanController.Hardware
{
    /// <summary>
    /// EC write operations for fan control
    /// </summary>
    internal static class ECWriter
    {
        private const ushort EC_ADDR_PORT = 0x4E;
        private const ushort EC_DATA_PORT = 0x4F;

        public static void WriteECByte(ushort addr, byte value)
        {
            HardwareAccessPolicy.RequireLegacyWriteAccess();
            lock (ECUtils.IoLock)
            {
                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2E);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, 0x11);
                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2F);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, (byte)((addr >> 8) & 0xFF));

                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2E);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, 0x10);
                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2F);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, (byte)(addr & 0xFF));

                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2E);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, 0x12);
                PawnIODriver.WriteIoPortByte(EC_ADDR_PORT, 0x2F);
                PawnIODriver.WriteIoPortByte(EC_DATA_PORT, value);
            }
        }

        private static void WriteECByteArray(ushort startAddr, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                WriteECByte((ushort)(startAddr + i), data[i]);
            }
        }

        public static void WriteFanAcclDeccl(int legionGen, byte[] acclValues, byte[] declValues)
        {
            if (legionGen == 5)
            {
                byte fan1Accl = acclValues.Length > 0 ? acclValues[0] : (byte)2;
                byte fan2Accl = acclValues.Length > 1 ? acclValues[1] : fan1Accl;
                byte fan1Decl = declValues.Length > 0 ? declValues[0] : (byte)2;
                byte fan2Decl = declValues.Length > 1 ? declValues[1] : fan1Decl;

                WriteECByte((ushort)ECWriteRegisters.FAN1_ACC_GEN5, fan1Accl);
                WriteECByte((ushort)ECWriteRegisters.FAN1_DEC_GEN5, fan1Decl);
                WriteECByte((ushort)ECWriteRegisters.FAN2_ACC_GEN5, fan2Accl);
                WriteECByte((ushort)ECWriteRegisters.FAN2_DEC_GEN5, fan2Decl);
            }
            else
            {
                WriteECByteArray((ushort)ECWriteRegisters.FAN_ACC_GEN6, PadWithLastValue(acclValues, 10, 2));
                WriteECByteArray((ushort)ECWriteRegisters.FAN_DEC_GEN6, PadWithLastValue(declValues, 10, 2));
            }
        }

        public static void WriteFanPointCount(byte pointCount)
        {
            WriteECByte((ushort)ECWriteRegisters.FAN_POINTS_NO, pointCount);
        }

        public static void WriteFanRpmPoints(byte[] fan1RpmPoints, byte[] fan2RpmPoints)
        {
            WriteECByteArray((ushort)ECWriteRegisters.FAN1_RPM_ST_ADDR, PadWithLastValue(fan1RpmPoints, 9, 0));
            WriteECByteArray((ushort)ECWriteRegisters.FAN2_RPM_ST_ADDR, PadWithLastValue(fan2RpmPoints, 9, 0));
        }

        public static void WriteTemperatureRamp(byte[] rampUpValues, byte[] rampDownValues,
            ushort rampUpStartAddr, ushort rampDownStartAddr)
        {
            const byte IGNORE_VALUE = 0x7F;  // Lenovo EC ignore/disable marker

            WriteECByteArray(rampUpStartAddr, PadValues(rampUpValues, 10, IGNORE_VALUE));
            WriteECByteArray(rampDownStartAddr, PadValues(rampDownValues, 10, 0));
        }

        private static byte[] PadValues(byte[] values, int length, byte emptyValue)
        {
            byte[] result = Enumerable.Repeat(emptyValue, length).ToArray();
            Array.Copy(values, result, Math.Min(values.Length, length));
            return result;
        }

        private static byte[] PadWithLastValue(byte[] values, int length, byte emptyValue)
        {
            byte fillValue = values.Length > 0 ? values[Math.Min(values.Length, length) - 1] : emptyValue;
            return PadValues(values, length, fillValue);
        }

        public static void BeginFanTableUpdate()
        {
            WriteFanTableChangeCounter(0);
        }

        public static void ResetFanCurveState()
        {
            WriteECByte((ushort)ECWriteRegisters.FAN_CUR_POINT, 0);
            WriteECByte((ushort)ECWriteRegisters.CPU_FAN_LEVEL, 0);
            WriteECByte((ushort)ECWriteRegisters.GPU_FAN_LEVEL, 0);
            WriteECByte((ushort)ECWriteRegisters.HST_FAN_LEVEL, 0);
        }

        public static void WriteStopRgbFanWake()
        {
            WriteECByte((ushort)ECWriteRegisters.STOP_RGB_FAN_WAKE, 0x25);
        }

        public static void WriteFanTableChangeCounter(byte value)
        {
            WriteECByte((ushort)ECWriteRegisters.FAN_TABLE_CHG_COUNTER, value);
            WriteECByte((ushort)ECWriteRegisters.FAN_TABLE_CHG_COUNTER_SEC, value);
        }
    }

    internal enum ECWriteRegisters : ushort
    {
        // Gen5 ACC/DEC
        FAN1_ACC_GEN5 = 0xC3DC,
        FAN1_DEC_GEN5 = 0xC3DD,
        FAN2_ACC_GEN5 = 0xC3DE,
        FAN2_DEC_GEN5 = 0xC3DF,

        // Gen6 ACC/DEC
        FAN_ACC_GEN6 = 0xC560,
        FAN_DEC_GEN6 = 0xC570,

        // Fan points
        FAN_POINTS_NO = 0xC535,

        // Fan RPM tables
        FAN1_RPM_ST_ADDR = 0xC551,
        FAN2_RPM_ST_ADDR = 0xC541,

        // Temperature thresholds
        CPU_RAMP_UP = 0xC580,
        CPU_RAMP_DOWN = 0xC591,
        GPU_RAMP_UP = 0xC5A0,
        GPU_RAMP_DOWN = 0xC5B1,
        HST_RAMP_UP = 0xC5C0,
        HST_RAMP_DOWN = 0xC5D1,

        // Misc
        STOP_RGB_FAN_WAKE = 0xC64D,
        FAN_TABLE_CHG_COUNTER = 0xC5FE,
        FAN_TABLE_CHG_COUNTER_SEC = 0xC5FF,
        FAN_CUR_POINT = 0xC534,
        CPU_FAN_LEVEL = 0xC634,
        GPU_FAN_LEVEL = 0xC635,
        HST_FAN_LEVEL = 0xC636,
    }
}
