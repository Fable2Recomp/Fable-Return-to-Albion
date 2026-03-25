/*
** $Id: lundump.c,v 2.7.1.4 2008/04/04 19:51:41 roberto Exp $
** load precompiled Lua chunks
** See Copyright Notice in lua.h
*/

#include <string.h>
#include <limits.h>

#define lundump_c
#define LUA_CORE

#include "lua.h"

#include "ldebug.h"
#include "ldo.h"
#include "lfunc.h"
#include "lmem.h"
#include "lobject.h"
#include "lstring.h"
#include "lundump.h"
#include "lzio.h"

typedef struct {
 lua_State* L;
 ZIO* Z;
 Mbuffer* b;
 const char* name;
 int swap;
 int sizeof_int;
 int sizeof_size_t;
 int sizeof_instruction;
 int sizeof_number;
 int number_integral;
} LoadState;

#ifdef LUAC_TRUST_BINARIES
#define IF(c,s)
#define error(S,s)
#else
#define IF(c,s)		if (c) error(S,s)

static void error(LoadState* S, const char* why)
{
 luaO_pushfstring(S->L,"%s: %s in precompiled chunk",S->name,why);
 luaD_throw(S->L,LUA_ERRSYNTAX);
}
#endif

#define LoadMem(S,b,n,size)	LoadBlock(S,b,(n)*(size))
#define	LoadByte(S)		(lu_byte)LoadChar(S)
#define LoadVar(S,x)		LoadMem(S,&x,1,sizeof(x))
#define LoadVector(S,b,n,size)	LoadMem(S,b,n,size)

static void LoadBlock(LoadState* S, void* b, size_t size)
{
 size_t r=luaZ_read(S->Z,b,size);
 IF (r!=0, "unexpected end");
}

static int LoadChar(LoadState* S)
{
 char x;
 LoadVar(S,x);
 return x;
}

static unsigned long long LoadUnsigned(LoadState* S, int size)
{
 unsigned char bytes[8];
 unsigned long long value=0;
 int i;
 IF (size<=0 || size>(int)sizeof(bytes), "unsupported scalar size");
 LoadBlock(S,bytes,size);
 if (S->swap)
 {
  for (i=0; i<size; i++)
   value=(value<<8)|bytes[i];
 }
 else
 {
  for (i=size-1; i>=0; i--)
   value=(value<<8)|bytes[i];
 }
 return value;
}

static int LoadInt(LoadState* S)
{
 unsigned long long x=LoadUnsigned(S,S->sizeof_int);
 IF (x>(unsigned long long)INT_MAX, "bad integer");
 return (int)x;
}

static size_t LoadSize(LoadState* S)
{
 return (size_t)LoadUnsigned(S,S->sizeof_size_t);
}

static lua_Number LoadNumber(LoadState* S)
{
 if (S->number_integral)
  return (lua_Number)LoadInt(S);
 if (S->sizeof_number==4)
 {
  union { float f; unsigned char b[4]; } u;
  int i;
  LoadBlock(S,u.b,4);
  if (S->swap)
   for (i=0; i<2; i++) { unsigned char t=u.b[i]; u.b[i]=u.b[3-i]; u.b[3-i]=t; }
  return (lua_Number)u.f;
 }
 else if (S->sizeof_number==8)
 {
  union { double d; unsigned char b[8]; } u;
  int i;
  LoadBlock(S,u.b,8);
  if (S->swap)
   for (i=0; i<4; i++) { unsigned char t=u.b[i]; u.b[i]=u.b[7-i]; u.b[7-i]=t; }
  return (lua_Number)u.d;
 }
 error(S,"unsupported number format");
 return 0;
}

static Instruction LoadInstruction(LoadState* S)
{
 return (Instruction)LoadUnsigned(S,S->sizeof_instruction);
}

static TString* LoadString(LoadState* S)
{
 size_t size=LoadSize(S);
 if (size==0)
  return NULL;
 else
 {
  char* s=luaZ_openspace(S->L,S->b,size);
  LoadBlock(S,s,size);
  return luaS_newlstr(S->L,s,size-1);		/* remove trailing '\0' */
 }
}

static void LoadCode(LoadState* S, Proto* f)
{
 int i,n=LoadInt(S);
 f->code=luaM_newvector(S->L,n,Instruction);
 f->sizecode=n;
 for (i=0; i<n; i++) f->code[i]=LoadInstruction(S);
}

static Proto* LoadFunction(LoadState* S, TString* p);

static void LoadConstants(LoadState* S, Proto* f)
{
 int i,n;
 n=LoadInt(S);
 f->k=luaM_newvector(S->L,n,TValue);
 f->sizek=n;
 for (i=0; i<n; i++) setnilvalue(&f->k[i]);
 for (i=0; i<n; i++)
 {
  TValue* o=&f->k[i];
  int t=LoadChar(S);
  switch (t)
  {
   case LUA_TNIL:
   	setnilvalue(o);
	break;
   case LUA_TBOOLEAN:
   	setbvalue(o,LoadChar(S)!=0);
	break;
   case LUA_TNUMBER:
	setnvalue(o,LoadNumber(S));
	break;
   case LUA_TSTRING:
	setsvalue2n(S->L,o,LoadString(S));
	break;
   default:
	error(S,"bad constant");
	break;
  }
 }
 n=LoadInt(S);
 f->p=luaM_newvector(S->L,n,Proto*);
 f->sizep=n;
 for (i=0; i<n; i++) f->p[i]=NULL;
 for (i=0; i<n; i++) f->p[i]=LoadFunction(S,f->source);
}

static void LoadDebug(LoadState* S, Proto* f)
{
 int i,n;
 n=LoadInt(S);
 f->lineinfo=luaM_newvector(S->L,n,int);
 f->sizelineinfo=n;
 for (i=0; i<n; i++) f->lineinfo[i]=LoadInt(S);
 n=LoadInt(S);
 f->locvars=luaM_newvector(S->L,n,LocVar);
 f->sizelocvars=n;
 for (i=0; i<n; i++) f->locvars[i].varname=NULL;
 for (i=0; i<n; i++)
 {
  f->locvars[i].varname=LoadString(S);
  f->locvars[i].startpc=LoadInt(S);
  f->locvars[i].endpc=LoadInt(S);
 }
 n=LoadInt(S);
 f->upvalues=luaM_newvector(S->L,n,TString*);
 f->sizeupvalues=n;
 for (i=0; i<n; i++) f->upvalues[i]=NULL;
 for (i=0; i<n; i++) f->upvalues[i]=LoadString(S);
}

static Proto* LoadFunction(LoadState* S, TString* p)
{
 Proto* f;
 if (++S->L->nCcalls > LUAI_MAXCCALLS) error(S,"code too deep");
 f=luaF_newproto(S->L);
 setptvalue2s(S->L,S->L->top,f); incr_top(S->L);
 f->source=LoadString(S); if (f->source==NULL) f->source=p;
 f->linedefined=LoadInt(S);
 f->lastlinedefined=LoadInt(S);
 f->nups=LoadByte(S);
 f->numparams=LoadByte(S);
 f->is_vararg=LoadByte(S);
 f->maxstacksize=LoadByte(S);
 LoadCode(S,f);
 LoadConstants(S,f);
 LoadDebug(S,f);
 IF (!luaG_checkcode(f), "bad code");
 S->L->top--;
 S->L->nCcalls--;
 return f;
}

static void LoadHeader(LoadState* S)
{
 char s[LUAC_HEADERSIZE];
 LoadBlock(S,s,LUAC_HEADERSIZE);
 IF (memcmp(s,LUA_SIGNATURE,sizeof(LUA_SIGNATURE)-1)!=0, "bad signature");
 IF ((unsigned char)s[sizeof(LUA_SIGNATURE)-1]!=(unsigned char)LUAC_VERSION, "bad version");
 IF ((unsigned char)s[sizeof(LUA_SIGNATURE)]!=(unsigned char)LUAC_FORMAT, "bad format");
 S->swap=((unsigned char)s[sizeof(LUA_SIGNATURE)+1] != (*(char*)&(int){1}));
 S->sizeof_int=(unsigned char)s[sizeof(LUA_SIGNATURE)+2];
 S->sizeof_size_t=(unsigned char)s[sizeof(LUA_SIGNATURE)+3];
 S->sizeof_instruction=(unsigned char)s[sizeof(LUA_SIGNATURE)+4];
 S->sizeof_number=(unsigned char)s[sizeof(LUA_SIGNATURE)+5];
 S->number_integral=(unsigned char)s[sizeof(LUA_SIGNATURE)+6];
 IF (S->sizeof_int!=4, "unsupported int size");
 IF (S->sizeof_instruction!=4, "unsupported instruction size");
 IF (!(S->sizeof_size_t==4 || S->sizeof_size_t==8), "unsupported size_t size");
 IF (!(S->sizeof_number==4 || S->sizeof_number==8), "unsupported number size");
}

/*
** load precompiled chunk
*/
Proto* luaU_undump (lua_State* L, ZIO* Z, Mbuffer* buff, const char* name)
{
 LoadState S;
 if (*name=='@' || *name=='=')
  S.name=name+1;
 else if (*name==LUA_SIGNATURE[0])
  S.name="binary string";
 else
  S.name=name;
 S.L=L;
 S.Z=Z;
 S.b=buff;
 LoadHeader(&S);
 return LoadFunction(&S,luaS_newliteral(L,"=?"));
}

/*
* make header
*/
void luaU_header (char* h)
{
 int x=1;
 memcpy(h,LUA_SIGNATURE,sizeof(LUA_SIGNATURE)-1);
 h+=sizeof(LUA_SIGNATURE)-1;
 *h++=(char)LUAC_VERSION;
 *h++=(char)LUAC_FORMAT;
 *h++=(char)*(char*)&x;				/* endianness */
 *h++=(char)sizeof(int);
 *h++=(char)sizeof(size_t);
 *h++=(char)sizeof(Instruction);
 *h++=(char)sizeof(lua_Number);
 *h++=(char)(((lua_Number)0.5)==0);		/* is lua_Number integral? */
}
