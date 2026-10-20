/* Self-owned offline DWARF fixture. Never flash. */
#include <stdint.h>
#include <stddef.h>
typedef struct { uint8_t tag; float speed; int16_t error; } Feedback;
typedef struct { uint32_t count; Feedback feedback; float gains[3]; } Motor;
volatile Motor motors[2];
static Motor chassis;
struct __attribute__((packed)) Packed { uint8_t tag; uint32_t count; } packed_state;
struct Edge { unsigned flag : 1; uint32_t *pointer; union { float f; uint32_t u; } either; } edge;
float matrix[2][3];
_Static_assert(sizeof(Feedback) == 12 && offsetof(Feedback, speed) == 4, "feedback layout");
_Static_assert(sizeof(Motor) == 28 && offsetof(Motor, feedback) == 4 && offsetof(Motor, gains) == 16, "motor layout");
_Static_assert(sizeof(struct Packed) == 5 && offsetof(struct Packed, count) == 1, "packed layout");
int main(void) {
    static Motor local_static;
    Motor stack_local;
    stack_local.count = motors[0].count;
    chassis.count = local_static.count++;
    return (int)(chassis.count + stack_local.count);
}
