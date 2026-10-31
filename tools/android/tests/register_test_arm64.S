// Test helper: hold independent sentinels in every AAPCS64 callee-saved GPR
// and the low 64 bits of v8-v15 across a callback that switches fibers.
// Returns a failure bitmask, restores the caller's original registers.
.text
.p2align 2
.global refii_fiber_register_test
.type refii_fiber_register_test, %function
refii_fiber_register_test:
  sub sp, sp, #192
  stp x19, x20, [sp, #0]
  stp x21, x22, [sp, #16]
  stp x23, x24, [sp, #32]
  stp x25, x26, [sp, #48]
  stp x27, x28, [sp, #64]
  stp x29, x30, [sp, #80]
  stp d8, d9, [sp, #96]
  stp d10, d11, [sp, #112]
  stp d12, d13, [sp, #128]
  stp d14, d15, [sp, #144]
  str x2, [sp, #160]

  add x19, x2, #19
  add x20, x2, #20
  add x21, x2, #21
  add x22, x2, #22
  add x23, x2, #23
  add x24, x2, #24
  add x25, x2, #25
  add x26, x2, #26
  add x27, x2, #27
  add x28, x2, #28
  add x29, x2, #29
  .macro SET_D reg, offset
    add x9, x2, #\offset
    fmov \reg, x9
  .endm
  SET_D d8, 108
  SET_D d9, 109
  SET_D d10, 110
  SET_D d11, 111
  SET_D d12, 112
  SET_D d13, 113
  SET_D d14, 114
  SET_D d15, 115

  mov x16, x0
  mov x0, x1
  blr x16

  mov x0, #0
  ldr x10, [sp, #160]
  .macro CHECK_X reg, offset, bit
    add x9, x10, #\offset
    cmp \reg, x9
    cset x11, ne
    orr x0, x0, x11, lsl #\bit
  .endm
  CHECK_X x19, 19, 0
  CHECK_X x20, 20, 1
  CHECK_X x21, 21, 2
  CHECK_X x22, 22, 3
  CHECK_X x23, 23, 4
  CHECK_X x24, 24, 5
  CHECK_X x25, 25, 6
  CHECK_X x26, 26, 7
  CHECK_X x27, 27, 8
  CHECK_X x28, 28, 9
  CHECK_X x29, 29, 10
  .macro CHECK_D reg, offset, bit
    add x9, x10, #\offset
    fmov x12, \reg
    cmp x12, x9
    cset x11, ne
    orr x0, x0, x11, lsl #\bit
  .endm
  CHECK_D d8, 108, 11
  CHECK_D d9, 109, 12
  CHECK_D d10, 110, 13
  CHECK_D d11, 111, 14
  CHECK_D d12, 112, 15
  CHECK_D d13, 113, 16
  CHECK_D d14, 114, 17
  CHECK_D d15, 115, 18
  mov x9, sp
  tst x9, #15
  cset x11, ne
  orr x0, x0, x11, lsl #19

  ldp x19, x20, [sp, #0]
  ldp x21, x22, [sp, #16]
  ldp x23, x24, [sp, #32]
  ldp x25, x26, [sp, #48]
  ldp x27, x28, [sp, #64]
  ldp x29, x30, [sp, #80]
  ldp d8, d9, [sp, #96]
  ldp d10, d11, [sp, #112]
  ldp d12, d13, [sp, #128]
  ldp d14, d15, [sp, #144]
  add sp, sp, #192
  ret
.size refii_fiber_register_test, .-refii_fiber_register_test
.section .note.GNU-stack,"",%progbits
