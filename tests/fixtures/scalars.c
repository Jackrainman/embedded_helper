/* Synthetic DWARF input only: never flash this executable. */
volatile signed char signed8 = -8;
volatile unsigned char unsigned8 = 8;
volatile short signed16 = -16;
volatile unsigned short unsigned16 = 16;
volatile int signed32 = -32;
volatile unsigned int unsigned32 = 32;
volatile float float32 = 1.25f;
volatile long long unsupported64 = 64;
int main(void) { return signed8; }
