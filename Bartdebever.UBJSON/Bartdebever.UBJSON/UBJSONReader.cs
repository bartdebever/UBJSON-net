using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;

namespace Bartdebever.UBJSON
{
    public class UBJSONReader
    {
        // Offset from where to read the data.
        private int _offset = 0;

        // The byte array to be read.
        private readonly byte[] _data;

        public UBJSONReader(byte[] data)
        {
            _data = data;
        }

        /// <summary>
        /// Decodes the provided byte array into a single C# object.
        /// </summary>
        /// <returns>The read C# object.</returns>
        public object Read()
        {
            return DecodeValue();
        }

        public TObject Read<TObject>()
            where TObject : class, new()
        {
            var dataObject = DecodeValue();
            if (dataObject is not Dictionary<string, object> stringDictionary)
            {
                throw new Exception("Cant convert UBJSON that didn't start with {");
            }

            // Converting this generic value into an object is a difficult task.
            // A simple algorithm was written before that could do this with reflection.
            // While this does work, the support for nested classes, dictionaries and arrays of data types
            // and other edge cases were incredibly difficult to program.
            // I'm not an expert in Reflection and lack the knowledge to write a complex method that can convert
            // this dictionary into a proper C# object.
            var converterOptions = new JsonSerializerOptions()
            {
                PropertyNameCaseInsensitive = true
            };
            return JsonSerializer.Deserialize<TObject>(JsonSerializer.Serialize(stringDictionary), converterOptions);
        }

        private Dictionary<string, object> DecodeObject()
        {
            var dictionary = new Dictionary<string, object>();
            var propertyCount = int.MaxValue;

            // Loop until we are out of data.
            while (_data.Length > _offset)
            {
                var lookahead = GetDataType();
                switch (lookahead)
                {
                    case '#':
                        // # indicates that the following value is the amount
                        // of properties which are present within the object.
                        // No closing bracket has to be provided after the amount
                        // of properties is reached.
                        propertyCount = Convert.ToInt32(DecodeValue());
                        break;
                    case '}':
                        // Increment the offset as if the '}' was read.
                        _offset += 1;
                        return dictionary;
                }

                // Keys of an object are not annotated with the S character
                // To imply they should be read as a string.
                // Normal strings are formatted like S U 0x05 h e l l o
                // But object keys are formatted like U 0x05 h e l l o
                // As for this reason, whenever we read the next key, we infer to the decoder
                // that the read type character was a string.
                var key = DecodeValue('S') as string;
                // Values of the properties do have a type definition and thus are read normally.
                var value = DecodeValue();
                dictionary.Add(key, value);

                // If the amount of requested properties are present within the "object"
                // it is returned.
                if (dictionary.Keys.Count >= propertyCount)
                {
                    return dictionary;
                }
            }

            // Object was not finalized before the stream ended, throw an exception.
            throw new Exception("Failed to parse object");
        }

        private ICollection<object> DecodeArray()
        {
            // Setup a possible array but leave the initialization null.
            // Arrays MAY specify the length they have, in that case
            // its more memory efficient to create an array with that length instead
            // of a list.
            ICollection<object> arrayValues = null;

            // Set the elementCount to max to ensure its never triggered on accident.
            var elementCount = int.MaxValue; 

            // Arrays may define their type once using the $ character.
            // This type needs to be stored and inferred when decoding the other values.
            // See the comments on the switch case bellow for more information.
            char? inferredType = null;

            // Parse for as long as the data allows for.
            while (_data.Length > _offset)
            {
                // When getting the type, a lookahead is done to ensure
                // the offset isn't increased on accident.
                var lookaheadCharacter = GetDataType();
                switch (lookaheadCharacter)
                {
                    case '#':
                        // Increase the offset as the # character is read and its not needed for decoding the next value.
                        _offset++;
                        // The # character indicates that the next provided value is the amount of items within the array.
                        // For example "[ # 0x03 ..." would indicate that there are 0x03 items within the array.
                        // The array does not close itself with a closing ] bracket if the amount is specified. 
                        elementCount = Convert.ToInt32(DecodeValue());

                        // Create the array with the specified length
                        arrayValues = new object[elementCount];
                        continue;
                    case '$':
                        // Increase the offset as the $ character is read and its not needed for decoding the next value.
                        _offset++;

                        // The type read after the $ is the inferred type of the array.
                        // As stated on Wikipedia:
                        // For example, the array ["a","b","c"] may be represented as [ $ C # U 0x03 a b c
                        inferredType = GetDataType();

                        // Increase the offset again because the type is read and its not needed for decoding the next value.
                        _offset++;
                        continue;
                    case ']':
                        // The closing ] bracket was provided and the array needs to be ended.
                        // If an array does not specify the amount of items, it will provide this character instead.
                        // Increment the offset as if the ']' was read and is not needed for further processing.
                        _offset++;
                        return arrayValues;
                }

                
                

                arrayValues ??= new List<object>();

                var value = DecodeValue(inferredType);
                arrayValues.Add(value);

                // If the number of items was provided, check if the array has already exceeded this count.
                // The item count is initialized to int.MaxValue so if it was never initialized, it should fail.
                if (arrayValues.Count >= elementCount)
                {
                    return arrayValues;
                }
            }
            
            // No closing bracket or right amount of elements was found, thus the data can no longer be parsed.
            throw new Exception("Failed to parse object");
        }

        /// <summary>
        /// Gets the type character using a lookahead operation.
        /// </summary>
        /// <returns>The next type character.</returns>
        private char GetDataType()
        {
            return Convert.ToChar(_data[_offset]);
        }

        private object DecodeValue(char? inferredChar = null, bool lookForward = false)
        {
            // Get the type character if the inferred character was not provided.
            var convertedChar = inferredChar ?? GetDataType();
            if (!lookForward && inferredChar == null)
            {
                _offset++;
            }
            switch (convertedChar)
            {
                case '{':
                    return DecodeObject();
                case '}':
                    return '}';
                case '[':
                    return DecodeArray();
                case ']':
                    return ']';
                case 'Z':
                    // Value is null
                    return null;
                case 'N':
                    // No operation
                    break;
                case 'T':
                    return true;
                case 'F':
                    return false;
                case 'C':
                    // ASCII char
                    break;
                case 'i':
                    var int8 = ReadInt8(_data, _offset + 1);
                    if (!lookForward)
                    {
                        _offset += 1;
                    }

                    return int8;
                case 'U':
                    var uint8 = ReadUInt8(_data, _offset + 1);
                    if (!lookForward)
                    {
                        _offset += 1;
                    }

                    return uint8;
                case 'I':
                    var shortValue = ReadShort(_data, _offset + 1);
                    if (!lookForward)
                    {
                        _offset += 2;
                    }

                    return shortValue;
                case 'l':
                    var intValue = ReadInt(_data, _offset + 1);
                    if (!lookForward)
                    {
                        _offset += 4;
                    }

                    return intValue;
                case 'd':
                    if (!lookForward)
                    {
                        _offset += 2;
                    }
                    return "Float32";
                case 'D':
                    if (!lookForward)
                    {
                        _offset += 4;
                    }
                    return "Float64";
                // String and Larger numeric values are stored as UTF-8.
                // There is no explicit type for this numeric value and such it is treated just like a string.
                case 'S':
                case 'H':
                    // UTF-8 string
                    return ReadUtf8String(lookForward);
            }

            return null;
        }

        /**
         * For the next section of the conversions into numbers,
         * keep in mind that the UBJSON format specifies numbers
         * are always stored within the Big Endian format.
         */
        private static byte ReadUInt8(byte[] payload, int startIndex)
        {
            return (byte)BitConverter.ToChar(payload, startIndex);
        }

        private static sbyte ReadInt8(byte[] payload, int startIndex)
        {
            return (sbyte)BitConverter.ToChar(payload, startIndex);
        }

        private static short ReadShort(byte[] payload, int startIndex)
        {
            var endIndex = startIndex + 2;
            return BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(startIndex..endIndex));
        }

        private static int ReadInt(byte[] payload, int startIndex)
        {
            var endIndex = startIndex + 4;
            return BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(startIndex..endIndex));
        }

        private string ReadUtf8String(bool lookAhead)
        {
            // Gets the type character of the string that is provided.
            // String formats are mostly written as S U 0x07
            // Where the U indicates the amount of characters to follow.
            var stringLengthType = Convert.ToChar(_data[_offset]);

            // In some special cases, the string doesn't specify the type or the parser
            // has already read it on accident.
            // Either way, if the type is able to be parsed to an int, treat it as the string length.
            if (!int.TryParse(stringLengthType.ToString(), out var stringLength))
            {
                // If this parsing failed, the length is the next byte.
                stringLength = _data[_offset + 1];
            }

            // Create a new array to gather the characters in.
            var stringCharArray = new char[stringLength];

            // For each character in the string.
            for (var i = 0; i < stringLength; i++)
            {
                // Convert the byte data to characters.
                stringCharArray[i] = Convert.ToChar(_data[_offset + 2 + i]);
            }

            // If this is not a lookahead, increase the offset by 2
            // To account for the numeric type character and the length.
            // and then by the length of the string.
            if (!lookAhead)
            {
                _offset += 2 + stringLength;
            }

            // Return the newly formed string using the character array.
            return new string(stringCharArray);
        }
    }
}
