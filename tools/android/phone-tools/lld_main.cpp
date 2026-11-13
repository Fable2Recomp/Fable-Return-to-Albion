// librefii_lld.so: LLD's ELF linker alone. Clang runs it through --ld-path under this file name, which
// names no LLD flavor, and lldMain reads the flavor from the name unless the first argument says it.
#include <vector>

#include "lld/Common/Driver.h"
#include "llvm/Support/InitLLVM.h"
#include "llvm/Support/raw_ostream.h"

LLD_HAS_DRIVER(elf)

int main(int argc, const char** argv) {
  llvm::InitLLVM init(argc, argv);
  std::vector<const char*> args{argv[0], "-flavor", "gnu"};
  args.insert(args.end(), argv + 1, argv + argc);
  lld::Result result = lld::lldMain(args, llvm::outs(), llvm::errs(), {{lld::Gnu, &lld::elf::link}});
  return result.retCode;
}
