using System;

namespace APIHandler.Exceptions;

public class ConcurrentModificationException(string message, Exception? innerException = null) : Exception(message, innerException);
