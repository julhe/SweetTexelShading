
uint ToUINT_2(float value)
{
	return value * 3.0;
}

float FromUINT_2(uint value)
{
	return float(value & 0x00000003) / 3.0;
}

uint ToUINT_3(float value)
{
	return value* 7.0;
}

float FromUINT_3(uint value)
{
	return float(value & 0x00000007) / 7.0;
}

uint ToUINT_4(float value)
{
	return value * 15.0 ;
}

float FromUINT_4(uint value)
{
	return float(value & 0x0000000F) / 15.0;
}

uint ToUINT_5(float value)
{
	return uint(value * 31.0);
}

float FromUINT_5(uint value)
{
	return (value & 0x0000001F) / 31.0;
}

uint ToUINT_6(float value)
{
	return uint(value * 63.0);
}

float FromUINT_6(uint value)
{
	return (value & 0x0000003F) / 63.0;
}

uint ToUINT_7(float value)
{
	return uint(value * 127.0);
}

float FromUINT_7(uint value)
{
	return float(value & 0x0000007F) / 127.0;
}

uint ToUINT_8(float value)
{
    return uint(value * 255.0);
}

float FromUINT_8(uint value)
{
	return float(value & 0x000000FF) / 255.0;
}

uint ToUINT_9(float value)
{
    return uint(value * 511.0);
}

float FromUINT_9(uint value)
{
    return float(value & 0x000001FF) / 511.0;
}

uint ToUINT_10(float value)
{
    return uint(value * 1023.0);
}

float FromUINT_10(uint value)
{
    return float(value & 0x000003FF) / 1023.0;
}