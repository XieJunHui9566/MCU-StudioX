/* Compile only: compare static, address-taken and explicit near contracts. */
volatile unsigned char observed;
extern void remote(void);
extern void near remote_near(void);
static void local_direct(void) { observed += 1U; }
static void local_addressed(void) { observed += 2U; }
void (* volatile selected)(void) = local_addressed;
void public_function(void) { observed += 3U; }
void caller(void)
{
    local_direct();
    local_addressed();
    public_function();
    remote();
    remote_near();
    selected();
    observed += 4U;
}
