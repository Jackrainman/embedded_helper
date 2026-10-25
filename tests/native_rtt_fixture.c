/* Controlled DLL boundary for the real Worker; never accesses hardware. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define API __declspec(dllexport)
static ULONGLONG started;
static const char *mode;
static void log_call(const char *name) {
    const char *path = getenv("EH_NATIVE_LOG");
    FILE *f = path ? fopen(path, "a") : NULL;
    if (f) { fprintf(f, "%s\n", name); fclose(f); }
}
API int JLINKARM_EMU_SelectByUSBSN(int serial) { return serial == 1 ? 0 : -1; }
API const char *JLINKARM_Open(void) { mode = getenv("EH_NATIVE_MODE"); started = GetTickCount64(); log_call("open"); return NULL; }
API void JLINKARM_Close(void) { log_call("close"); }
API int JLINKARM_ExecCommand(const char *command, char *error, int size) { return 0; }
API int JLINKARM_TIF_Select(int value) { return 0; }
API void JLINKARM_SetSpeed(int value) {}
API int JLINKARM_Connect(void) { return 0; }
API int JLINKARM_SelectIP(const char *host, int port) { return 0; }
API int JLINKARM_ReadMemEx(unsigned address, unsigned count, void *data, unsigned access) { memset(data, 0, count); return count; }
API int JLINK_RTTERMINAL_Control(unsigned command, void *config) { log_call(command ? "rtt-stop" : "rtt-start"); return 0; }
API int JLINK_RTTERMINAL_Read(unsigned channel, void *data, unsigned count) { return 0; }
API int JLINKARM_EMU_GetList(int interfaces, void *entries, int count) {
    log_call("enumerate");
    if (interfaces != 1 || count < 1) return -1;
    unsigned serial = 1;
    if (mode && GetTickCount64() - started >= 500) {
        if (!strcmp(mode, "disconnected")) return 0;
        if (!strcmp(mode, "replaced")) serial = 2;
        if (!strcmp(mode, "enumeration-error")) return -1;
    }
    memset(entries, 0, 264);
    memcpy(entries, &serial, sizeof serial);
    ((unsigned char *)entries)[4] = 1;
    memcpy((unsigned char *)entries + 50, "Controlled fixture", 18);
    return 1;
}
